using System.Data;
using System.Security.Claims;

using Microsoft.Data.SqlClient;

using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Checker;

namespace Olymp.WebApp.Services.DatabaseMaintenance;

public sealed partial class DatabaseBackupService
{
    private async Task AuthorizeRestoreAsync(ClaimsPrincipal actor)
    {
        if (!CanRestore) throw new UnauthorizedAccessException();
        await AuthorizeAsync(actor);
    }

    public async Task<Backup[]> PreviewRestoreAsync(ClaimsPrincipal actor, string name, CancellationToken token = default)
    {
        await AuthorizeRestoreAsync(actor);
        if (!await _operation.WaitAsync(0, token)) throw new OperationValidationException(SiteResources.BackupBusy);
        try { return RestoreSet(await ReadFilesAsync(token), name); }
        finally { _operation.Release(); }
    }

    internal static Backup[] RestoreSet(Backup[] files, string name)
    {
        var selected = files.SingleOrDefault(item => item.Name == name && item.Readable) ?? throw new EntityNotFoundException();
        if (!selected.Differential) return [selected];
        var full = files.FirstOrDefault(item => item.Readable && !item.Differential && !item.CopyOnly
            && item.Id == selected.BaseId && item.CheckpointLsn.HasValue && item.CheckpointLsn == selected.BaseLsn)
            ?? throw new OperationValidationException(SiteResources.BackupNeedsFull);
        return [full, selected];
    }

    public async Task RestoreAsync(ClaimsPrincipal actor, Backup[] preview, CancellationToken token = default)
    {
        await AuthorizeRestoreAsync(actor);
        if (!await _operation.WaitAsync(0, token)) throw new OperationValidationException(SiteResources.BackupBusy);
        ICheckerManager? checkers = null;
        bool resume = false;
        try
        {
            if (preview.Length == 0) throw new EntityNotFoundException();
            var current = RestoreSet(await ReadFilesAsync(token), preview[^1].Name);
            if (!current.SequenceEqual(preview)) throw new OperationValidationException(SiteResources.BackupChanged);
            _state = new(true, false, preview[^1].Name);
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(token);
            foreach (var backup in current)
            {
                using var verify = connection.CreateCommand();
                verify.CommandTimeout = 0;
                verify.CommandText = "RESTORE VERIFYONLY FROM DISK = @path WITH FILE = 1" + (backup.Checksums ? ", CHECKSUM;" : ";");
                verify.Parameters.AddWithValue("@path", SqlPath(backup.Name));
                await verify.ExecuteNonQueryAsync(token);
            }
            using var files = await ReadFileListAsync(connection, current[0].Name, token);
            if (current.Length == 2)
            {
                using var diffFiles = await ReadFileListAsync(connection, current[1].Name, token);
                if (!FileLayout(files).SequenceEqual(FileLayout(diffFiles)))
                    throw new InvalidDataException("The differential changes the database file layout.");
            }
            using var restore = CreateRestoreCommand(connection, current, files);
            using var scope = _scopes.CreateScope();
            checkers = scope.ServiceProvider.GetRequiredService<ICheckerManager>();
            resume = checkers.Active;
            await checkers.SetActiveAsync(false, token);
            token.ThrowIfCancellationRequested();
            // The database replacement must not depend on the lifetime of a browser tab.
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
            using var check = connection.CreateCommand();
            check.CommandText = $"DBCC CHECKDB ({QuoteDatabase(_database)}) WITH NO_INFOMSGS, ALL_ERRORMSGS;";
            check.CommandTimeout = 0;
            using var reader = await check.ExecuteReaderAsync(CancellationToken.None);
            do
            {
                if (await reader.ReadAsync(CancellationToken.None))
                    throw new InvalidDataException("DBCC CHECKDB reported errors after restoration.");
            } while (await reader.NextResultAsync(CancellationToken.None));
            _state = new(false, false, current[^1].Name);
        }
        catch
        {
            _state = new(false, true, preview.LastOrDefault()?.Name);
            throw;
        }
        finally
        {
            SqlConnection.ClearAllPools();
            if (resume) await checkers!.SetActiveAsync(true, CancellationToken.None);
            _operation.Release();
        }
    }

    private static IEnumerable<string> FileLayout(DataTable files) => files.Rows.Cast<DataRow>()
        .Select(row => $"{row["FileId"]}:{row["LogicalName"]}:{row["Type"]}:{row["IsPresent"]}").Order(StringComparer.Ordinal);

    private async Task<DataTable> ReadFileListAsync(SqlConnection connection, string name, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = "RESTORE FILELISTONLY FROM DISK = @path WITH FILE = 1;";
        command.Parameters.AddWithValue("@path", SqlPath(name));
        using var reader = await command.ExecuteReaderAsync(token);
        var files = new DataTable();
        files.Load(reader);
        return files;
    }

    private SqlCommand CreateRestoreCommand(SqlConnection connection, Backup[] backups, DataTable files)
    {
        if (files.Rows.Count == 0) throw new InvalidDataException("The backup contains no database files.");
        var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.Parameters.AddWithValue("@database", _database);
        command.Parameters.AddWithValue("@full", SqlPath(backups[0].Name));
        var moves = new List<string>();
        for (int i = 0; i < files.Rows.Count; i++)
        {
            var file = files.Rows[i];
            string extension = file.Field<string>("Type") switch
            {
                "D" => "mdf",
                "L" => "ldf",
                _ => throw new InvalidDataException("Unsupported database file type.")
            };
            if (!file.Field<bool>("IsPresent")) throw new InvalidDataException("The backup contains absent database files.");
            command.Parameters.AddWithValue($"@logical{i}", file["LogicalName"]);
            command.Parameters.AddWithValue($"@physical{i}", $"/var/opt/mssql/data/{_database}_{file.Field<long>("FileID")}.{extension}");
            moves.Add($"MOVE @logical{i} TO @physical{i}");
        }
        string database = QuoteDatabase(_database);
        bool differential = backups.Length == 2;
        string diffSql = "";
        if (differential)
        {
            command.Parameters.AddWithValue("@diff", SqlPath(backups[1].Name));
            diffSql = $"RESTORE DATABASE {database} FROM DISK = @diff WITH FILE = 1, RECOVERY{(backups[1].Checksums ? ", CHECKSUM" : "")};";
        }
        command.CommandText = $"""
            BEGIN TRY
                ALTER DATABASE {database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                RESTORE DATABASE {database} FROM DISK = @full
                    WITH FILE = 1, REPLACE, {(differential ? "NORECOVERY" : "RECOVERY")}{(backups[0].Checksums ? ", CHECKSUM" : "")}, {string.Join(", ", moves)};
                {diffSql}
                ALTER DATABASE {database} SET MULTI_USER;
            END TRY
            BEGIN CATCH
                IF DATABASEPROPERTYEX(@database, N'Status') = N'ONLINE'
                    ALTER DATABASE {database} SET MULTI_USER;
                THROW;
            END CATCH;
            """;
        return command;
    }
}

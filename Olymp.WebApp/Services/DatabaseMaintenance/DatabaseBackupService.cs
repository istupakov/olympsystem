using System.Globalization;
using System.Security.Claims;
using System.Threading.Channels;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Olymp.Domain;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.DatabaseMaintenance;

public sealed partial class DatabaseBackupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DatabaseBackupService> _logger;
    private readonly string _connectionString;
    private readonly string _database;
    private readonly string _localPath;
    private readonly string _sqlPath;
    private readonly Channel<bool> _requests = Channel.CreateUnbounded<bool>();
    private readonly SemaphoreSlim _operation = new(1);
    private volatile Operation _state = new(false, false, null);

    public DatabaseBackupService(IOptions<DatabaseBackupOptions> options, IDbContextFactory<OlympContext> factory, IWebHostEnvironment environment,
        IServiceScopeFactory scopes, ILogger<DatabaseBackupService> logger)
    {
        _scopes = scopes;
        CanRestore = environment.IsDevelopment();
        _logger = logger;
        using var db = factory.CreateDbContext();
        var connection = new SqlConnectionStringBuilder(db.Database.GetConnectionString());
        _database = connection.InitialCatalog;
        connection.InitialCatalog = "master";
        _connectionString = connection.ConnectionString;
        _localPath = Path.GetFullPath(options.Value.LocalPath, environment.ContentRootPath);
        _sqlPath = options.Value.SqlPath;
    }

    public Operation State => _state;
    public bool CanRestore { get; }
    public sealed record Operation(bool Running, bool Failed, string? FileName);
    public sealed record Backup(string Name, long Size, DateTime Modified, DateTime? Created = null,
        Guid? Id = null, Guid? BaseId = null, bool Differential = false, bool CopyOnly = false,
        decimal? CheckpointLsn = null, decimal? BaseLsn = null, bool Checksums = false)
    {
        public bool Readable => Id.HasValue;
    }
    public sealed record Snapshot(Backup[] Files, long Free, long Total, bool CanCreateDifferential);

    private async Task AuthorizeAsync(ClaimsPrincipal actor)
    {
        await using var scope = _scopes.CreateAsyncScope();
        if (!(await scope.ServiceProvider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(actor, PolicyNames.Admin)).Succeeded)
            throw new UnauthorizedAccessException();
    }

    public async Task<Snapshot> ReadAsync(ClaimsPrincipal actor, CancellationToken token = default)
    {
        await AuthorizeAsync(actor);
        var files = await ReadFilesAsync(token);
        var drive = new DriveInfo(Path.GetPathRoot(_localPath)!);
        // On Unix, the longest mount point identifies the filesystem containing the backup directory.
        drive = DriveInfo.GetDrives().Where(item => _localPath == item.Name.TrimEnd(Path.DirectorySeparatorChar)
            || _localPath.StartsWith(item.Name.EndsWith(Path.DirectorySeparatorChar) ? item.Name : item.Name + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .OrderByDescending(item => item.Name.Length).FirstOrDefault() ?? drive;
        return new(files, drive.AvailableFreeSpace, drive.TotalSize, await HasBaseAsync(files, token));
    }

    private async Task<Backup[]> ReadFilesAsync(CancellationToken token)
    {
        var files = new List<Backup>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(token);
        foreach (string path in Directory.EnumerateFiles(_localPath).Order(StringComparer.Ordinal))
        {
            if (Path.GetExtension(path) is not (".bak" or ".pending")) continue;
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || (info.Name == _state.FileName && _state.Running)) continue;
            var backup = new Backup(info.Name, info.Length, info.LastWriteTimeUtc);
            if (info.Extension == ".bak")
            {
                try
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "RESTORE HEADERONLY FROM DISK = @path;";
                    command.Parameters.AddWithValue("@path", SqlPath(info.Name));
                    await using var reader = await command.ExecuteReaderAsync(token);
                    if (await reader.ReadAsync(token) && (reader["DatabaseName"] as string) == _database
                        && Convert.ToInt32(reader["Position"], CultureInfo.InvariantCulture) == 1
                        && Convert.ToInt32(reader["BackupType"], CultureInfo.InvariantCulture) is 1 or 5
                        && !(bool)reader["IsDamaged"])
                    {
                        backup = backup with
                        {
                            Created = (DateTime)reader["BackupFinishDate"],
                            Id = (Guid)reader["BackupSetGUID"],
                            BaseId = reader["DifferentialBaseGUID"] as Guid?,
                            Differential = Convert.ToInt32(reader["BackupType"], CultureInfo.InvariantCulture) == 5,
                            CopyOnly = (bool)reader["IsCopyOnly"],
                            CheckpointLsn = (decimal)reader["CheckpointLSN"],
                            BaseLsn = reader["DifferentialBaseLSN"] as decimal?,
                            Checksums = (bool)reader["HasBackupChecksums"]
                        };
                        // Multiple backup sets in one file are outside this interface's format.
                        if (await reader.ReadAsync(token)) backup = new(info.Name, info.Length, info.LastWriteTimeUtc);
                    }
                }
                catch (SqlException exception)
                {
                    _logger.LogWarning(exception, "Cannot read backup header for {FileName}", info.Name);
                }
            }
            files.Add(backup);
        }
        return [.. files.OrderByDescending(item => item.Created ?? item.Modified)];
    }

    public async Task StartAsync(ClaimsPrincipal actor, bool differential)
    {
        await AuthorizeAsync(actor);
        if (!await _operation.WaitAsync(0)) throw new OperationValidationException(SiteResources.BackupBusy);
        try
        {
            if (!Directory.Exists(_localPath)) throw new DirectoryNotFoundException(_localPath);
            _state = new(true, false, null);
            if (!_requests.Writer.TryWrite(differential)) throw new InvalidOperationException();
        }
        catch { _state = new(false, true, null); _operation.Release(); throw; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (bool differential in _requests.Reader.ReadAllAsync(stoppingToken))
        {
            string name = $"olymp-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}-{(differential ? "diff" : "full")}";
            string pending = name + ".pending";
            _state = new(true, false, pending);
            try
            {
                if (differential && !await HasBaseAsync(await ReadFilesAsync(stoppingToken), stoppingToken))
                    throw new OperationValidationException(SiteResources.BackupNeedsFull);
                await using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(stoppingToken);
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 0;
                command.CommandText = $"BACKUP DATABASE {QuoteDatabase(_database)} TO DISK = @path WITH {(differential ? "DIFFERENTIAL, " : "")}CHECKSUM, INIT; RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM;";
                command.Parameters.AddWithValue("@path", SqlPath(pending));
                await command.ExecuteNonQueryAsync(stoppingToken);
                File.Move(LocalPath(pending), LocalPath(name + ".bak"));
                _state = new(false, false, name + ".bak");
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Database backup failed for {FileName}", pending);
                _state = new(false, true, pending);
            }
            finally { _operation.Release(); }
        }
    }

    private async Task<bool> HasBaseAsync(Backup[] files, CancellationToken token)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DISTINCT differential_base_guid FROM {QuoteDatabase(_database)}.sys.database_files WHERE type = 0;";
        await using var reader = await command.ExecuteReaderAsync(token);
        bool found = false;
        while (await reader.ReadAsync(token))
        {
            found = true;
            if (reader.IsDBNull(0) || !files.Any(item => item.Readable && !item.Differential && !item.CopyOnly && item.Id == reader.GetGuid(0)))
                return false;
        }
        return found;
    }

    public async Task<Backup[]> PreviewDeleteAsync(ClaimsPrincipal actor, string name)
    {
        await AuthorizeAsync(actor);
        if (!await _operation.WaitAsync(0)) throw new OperationValidationException(SiteResources.BackupBusy);
        try { return DeletionSet(await ReadFilesAsync(CancellationToken.None), name); }
        finally { _operation.Release(); }
    }

    internal static Backup[] DeletionSet(Backup[] files, string name)
    {
        var selected = files.SingleOrDefault(item => item.Name == name) ?? throw new EntityNotFoundException();
        return [.. files.Where(item => item.Name == name || (selected.Readable && !selected.Differential && item.BaseId == selected.Id))
            .OrderBy(item => item.Differential ? 0 : 1).ThenBy(item => item.Name, StringComparer.Ordinal)];
    }

    public async Task DeleteAsync(ClaimsPrincipal actor, Backup[] preview)
    {
        await AuthorizeAsync(actor);
        if (!await _operation.WaitAsync(0)) throw new OperationValidationException(SiteResources.BackupBusy);
        try
        {
            var files = await ReadFilesAsync(CancellationToken.None);
            if (preview.Length == 0) throw new EntityNotFoundException();
            var current = DeletionSet(files, preview[^1].Name);
            if (!current.SequenceEqual(preview)) throw new OperationValidationException(SiteResources.BackupChanged);
            foreach (var file in current) File.Delete(LocalPath(file.Name));
        }
        finally { _operation.Release(); }
    }

    public async Task<FileStream[]> OpenDownloadAsync(ClaimsPrincipal actor, string name, CancellationToken token, bool single = false)
    {
        await AuthorizeAsync(actor);
        if (!await _operation.WaitAsync(0, token)) throw new OperationValidationException(SiteResources.BackupBusy);
        var streams = new List<FileStream>();
        try
        {
            var files = await ReadFilesAsync(token);
            var selected = files.SingleOrDefault(item => item.Name == name && item.Readable) ?? throw new EntityNotFoundException();
            if (selected.Differential && !single)
            {
                var full = files.FirstOrDefault(item => item.Id == selected.BaseId && !item.Differential && !item.CopyOnly)
                    ?? throw new OperationValidationException(SiteResources.BackupNeedsFull);
                streams.Add(File.OpenRead(LocalPath(full.Name)));
            }
            streams.Add(File.OpenRead(LocalPath(selected.Name)));
            return [.. streams];
        }
        catch { foreach (var stream in streams) await stream.DisposeAsync(); throw; }
        finally { _operation.Release(); }
    }

    private string LocalPath(string name)
    {
        if (name != Path.GetFileName(name) || name.Contains('\\') || name is "." or "..") throw new EntityNotFoundException();
        string path = Path.Combine(_localPath, name);
        if (new FileInfo(path).LinkTarget is not null) throw new EntityNotFoundException();
        return path;
    }
    private string SqlPath(string name) => _sqlPath.TrimEnd('/') + "/" + Path.GetFileName(LocalPath(name));
    private static string QuoteDatabase(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
}

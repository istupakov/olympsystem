#:project ../Olymp.Domain/Olymp.Domain.csproj
#:property PublishAot=false

using System.Data;
using Microsoft.Data.SqlClient;

if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Development")
{
    return Error("Run this task inside the Development container.");
}

if (args.Length != 1 || !args[0].EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
    || args[0].IndexOfAny(['/', '\\', '\r', '\n']) >= 0)
{
    return Error("Specify only a .bak filename from ./backup.");
}

var siteConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
if (string.IsNullOrEmpty(siteConnectionString))
{
    return Error("Run this task inside the Dev Container with ConnectionStrings__DefaultConnection configured.");
}
var password = new SqlConnectionStringBuilder(siteConnectionString).Password;

Console.WriteLine($"Restore {args[0]} into sqlserver/olympdb? Existing dev data will be replaced.");
Console.Write("Type RESTORE to continue: ");
if (Console.ReadLine() != "RESTORE")
{
    return Error("Restore cancelled.");
}

var connectionString = new SqlConnectionStringBuilder
{
    DataSource = "tcp:sqlserver,1433",
    InitialCatalog = "master",
    UserID = "sa",
    Password = password,
    TrustServerCertificate = true,
    ConnectTimeout = 10
};
using var connection = new SqlConnection(connectionString.ConnectionString);
connection.InfoMessage += (_, message) => Console.WriteLine(message.Message);
await connection.OpenAsync();

var backupPath = $"/var/opt/mssql/backup/{args[0]}";
using var files = new DataTable();
using (var command = connection.CreateCommand())
{
    command.CommandText = "RESTORE FILELISTONLY FROM DISK = @backupPath;";
    command.Parameters.Add("@backupPath", SqlDbType.NVarChar, 4000).Value = backupPath;
    using var reader = await command.ExecuteReaderAsync();
    files.Load(reader);
}

using var restore = CreateRestoreCommand(connection, backupPath, files);
await restore.ExecuteNonQueryAsync();
Console.WriteLine("Development database restored.");
return 0;

static int Error(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static SqlCommand CreateRestoreCommand(SqlConnection connection, string backupPath, DataTable files)
{
    if (files.Rows.Count == 0)
    {
        throw new InvalidDataException("No database files found in the backup.");
    }

    var command = connection.CreateCommand();
    command.CommandTimeout = 0;
    command.Parameters.Add("@backupPath", SqlDbType.NVarChar, 4000).Value = backupPath;
    List<string> moves = [];
    for (var i = 0; i < files.Rows.Count; i++)
    {
        var file = files.Rows[i];
        var extension = file.Field<string>("Type") switch
        {
            "D" => "mdf",
            "L" => "ldf",
            _ => throw new InvalidDataException($"Unsupported backup file type: {file["Type"]}")
        };
        command.Parameters.Add($"@logicalName{i}", SqlDbType.NVarChar, 128).Value = file["LogicalName"];
        command.Parameters.Add($"@filePath{i}", SqlDbType.NVarChar, 260).Value =
            $"/var/opt/mssql/data/olympdb_{file.Field<long>("FileID")}.{extension}";
        moves.Add($"MOVE @logicalName{i} TO @filePath{i}");
    }

    command.CommandText = $"""
        BEGIN TRY
            IF DB_ID(N'olympdb') IS NOT NULL
                ALTER DATABASE [olympdb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
            RESTORE DATABASE [olympdb] FROM DISK = @backupPath
                WITH REPLACE, RECOVERY, STATS = 10, {string.Join(", ", moves)};
            ALTER DATABASE [olympdb] SET MULTI_USER;
        END TRY
        BEGIN CATCH
            IF DATABASEPROPERTYEX(N'olympdb', N'Status') = N'ONLINE'
                ALTER DATABASE [olympdb] SET MULTI_USER;
            THROW;
        END CATCH;
        """;
    return command;
}

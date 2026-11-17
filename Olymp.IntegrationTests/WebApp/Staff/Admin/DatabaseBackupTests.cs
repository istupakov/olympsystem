using System.Net;
using System.Security.Claims;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Olymp.Domain;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.DatabaseMaintenance;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Admin;

public class DatabaseBackupTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData("DatabaseBackups:LocalPath", "")]
    [InlineData("DatabaseBackups:LocalPath", null)]
    [InlineData("DatabaseBackups:SqlPath", "")]
    [InlineData("DatabaseBackups:SqlPath", null)]
    public void InvalidBackupSettingsFailStartup(string key, string? value)
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));
        Assert.Throws<OptionsValidationException>(() => host.Services);
    }

    [Fact]
    public async Task MissingBackupDirectoryDoesNotPreventStartup()
    {
        string path = Path.Combine(Path.GetTempPath(), "missing-backups-" + Guid.NewGuid().ToString("N"));
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["DatabaseBackups:LocalPath"] = path })));
        var service = host.Services.GetRequiredService<DatabaseBackupService>();
        var actor = Staff((await _factory.AddUserAsync()).Id);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.ReadAsync(actor, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FullAndDiffRestoreTogetherAndDeletionRechecksDependencies()
    {
        var token = TestContext.Current.CancellationToken;
        var actor = Staff((await _factory.AddUserAsync()).Id);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        string connectionString = db.Database.GetConnectionString()!;
        var environment = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        var storage = _factory.Services.GetRequiredService<IOptions<DatabaseBackupOptions>>().Value;
        string folder = "backup-test-" + Guid.NewGuid().ToString("N");
        string localPath = Path.Combine(Path.GetFullPath(storage.LocalPath, environment.ContentRootPath), folder);
        string sqlPath = storage.SqlPath.TrimEnd('/') + "/" + folder;
        Directory.CreateDirectory(localPath);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(localPath,
            UnixFileMode.SetGroup | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        var options = Options.Create(new DatabaseBackupOptions
        {
            LocalPath = localPath,
            SqlPath = sqlPath
        });
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Warning));
        using var service = new DatabaseBackupService(options, _factory.Services.GetRequiredService<IDbContextFactory<OlympContext>>(), environment,
            _factory.Services.GetRequiredService<IServiceScopeFactory>(), loggerFactory.CreateLogger<DatabaseBackupService>());
        await service.StartAsync(token);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(new ClaimsPrincipal(), token));
            var jury = Staff(1, RoleNames.Jury);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.StartAsync(jury, false));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PreviewDeleteAsync(jury, "full.bak"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteAsync(jury, []));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.OpenDownloadAsync(jury, "full.bak", token));
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE BackupProbe (Value int NOT NULL); INSERT INTO BackupProbe VALUES (1);", token);
            await CreateAsync(service, actor, false, token);
            var full = Assert.Single((await service.ReadAsync(actor, token)).Files);
            Assert.True(full.Readable);
            Assert.False(full.CopyOnly);
            await db.Database.ExecuteSqlRawAsync("UPDATE BackupProbe SET Value = 2;", token);
            await CreateAsync(service, actor, true, token);
            var snapshot = await service.ReadAsync(actor, token);
            var diff = Assert.Single(snapshot.Files, item => item.Differential);
            Assert.Equal(full.Id, diff.BaseId);
            Assert.True(snapshot.Free > 0);
            var preview = await service.PreviewDeleteAsync(actor, full.Name);
            Assert.Equal(2, preview.Length);
            var streams = await service.OpenDownloadAsync(actor, diff.Name, token);
            try { Assert.Equal(2, streams.Length); Assert.All(streams, item => Assert.True(item.Length > 0)); }
            finally { foreach (var stream in streams) await stream.DisposeAsync(); }
            await RestoreAsync(connectionString, sqlPath, full.Name, diff.Name, token);
            await CreateAsync(service, actor, true, token);
            await Assert.ThrowsAsync<OperationValidationException>(() => service.DeleteAsync(actor, preview));
            var latest = await service.PreviewDeleteAsync(actor, full.Name);
            Assert.Equal(3, latest.Length);
            await service.DeleteAsync(actor, latest);
            Assert.Empty((await service.ReadAsync(actor, token)).Files);
            await service.StartAsync(actor, true);
            await WaitAsync(service, token);
            Assert.True(service.State.Failed);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            Directory.Delete(localPath, true);
        }
    }

    [Theory]
    [InlineData(RoleNames.Jury)]
    [InlineData(null)]
    public async Task NonAdminsCannotOpenBackupsOrDownloadFiles(string? role)
    {
        var user = await _factory.AddUserAsync();
        if (role is not null) await GrantRoleAsync(user.Id, role);
        using var client = await _factory.LoginAsync(user.UserName!);
        foreach (string path in new[] { "/admin/backups", "/admin/backups/full.bak/download" })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("AccessDenied", response.Headers.Location!.OriginalString);
        }
    }

    [Fact]
    public void DeletionOnlyIncludesDiffsOfTheSelectedFullBackup()
    {
        var id = Guid.NewGuid();
        DatabaseBackupService.Backup[] files = [new("full.bak", 10, default, Id: id),
            new("diff.bak", 5, default, Id: Guid.NewGuid(), BaseId: id, Differential: true),
            new("other.bak", 10, default, Id: Guid.NewGuid())];
        Assert.Equal(2, DatabaseBackupService.DeletionSet(files, "full.bak").Length);
        Assert.Single(DatabaseBackupService.DeletionSet(files, "diff.bak"));
        Assert.Single(DatabaseBackupService.DeletionSet(files, "other.bak"));
        Assert.Throws<EntityNotFoundException>(() => DatabaseBackupService.DeletionSet(files, "../full.bak"));
    }

    private static async Task CreateAsync(DatabaseBackupService service, ClaimsPrincipal actor, bool differential, CancellationToken token)
    {
        await service.StartAsync(actor, differential);
        await WaitAsync(service, token);
        Assert.False(service.State.Failed, service.State.FileName);
    }

    private static async Task WaitAsync(DatabaseBackupService service, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        while (service.State.Running) await Task.Delay(25, timeout.Token);
    }

    private static async Task RestoreAsync(string connectionString, string sqlPath, string full, string diff, CancellationToken token)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(token);
        string database = "BackupRestoreTest_" + Guid.NewGuid().ToString("N");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 90;
        command.CommandText = "RESTORE FILELISTONLY FROM DISK = @full;";
        command.Parameters.AddWithValue("@full", sqlPath + "/" + full);
        command.Parameters.AddWithValue("@diff", sqlPath + "/" + diff);
        var moves = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                string logical = ((string)reader["LogicalName"]).Replace("'", "''", StringComparison.Ordinal);
                moves.Add($"MOVE N'{logical}' TO N'/var/opt/mssql/data/{database}-{moves.Count}.dat'");
            }
        }
        try
        {
            command.CommandText = $"RESTORE DATABASE [{database}] FROM DISK = @full WITH NORECOVERY, {string.Join(", ", moves)}; RESTORE DATABASE [{database}] FROM DISK = @diff WITH RECOVERY; SELECT Value FROM [{database}].dbo.BackupProbe;";
            Assert.Equal(2, await command.ExecuteScalarAsync(token));
        }
        finally
        {
            command.CommandText = $"IF DB_ID(N'{database}') IS NOT NULL DROP DATABASE [{database}];";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}

using System.Net;
using System.Reflection;
using System.Security.Claims;

using AngleSharp.Html.Parser;

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
using Olymp.WebApp.Components.Pages.Staff.Admin;
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
        var logger = new BackupLogger();
        using var service = new DatabaseBackupService(options, _factory.Services.GetRequiredService<IDbContextFactory<OlympContext>>(), environment,
            _factory.Services.GetRequiredService<IServiceScopeFactory>(), logger);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(new ClaimsPrincipal(), token));
            var jury = Staff(1, RoleNames.Jury);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.StartAsync(jury, false));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PreviewDeleteAsync(jury, "full.bak"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteAsync(jury, []));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.OpenDownloadAsync(jury, "full.bak", token));
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE BackupProbe (Value int NOT NULL); INSERT INTO BackupProbe VALUES (1);", token);
            Assert.False((await service.ReadAsync(actor, token)).CanCreateDifferential);
            await BackupAsync(connectionString, sqlPath + "/copy-only.bak", true, token);
            var copyOnly = await service.ReadAsync(actor, token);
            Assert.True(Assert.Single(copyOnly.Files).CopyOnly);
            Assert.False(copyOnly.CanCreateDifferential);
            File.Delete(Path.Combine(localPath, "copy-only.bak"));
            await service.StartAsync(actor, false);
            var opening = RenderAsync<Backups>("/admin/backups", [], actor,
                initialize: page => SetService(page, service));
            Assert.False(opening.IsCompleted);
            await service.StartAsync(token);
            using (var opened = await new HtmlParser().ParseDocumentAsync(await opening.WaitAsync(TimeSpan.FromSeconds(90), token)))
            {
                Assert.False(service.State.Failed, logger.Error ?? service.State.FileName);
                Assert.NotNull(opened.QuerySelector(".alert-success .btn-close"));
                Assert.Null(opened.QuerySelector(".alert-info, .alert-danger"));
                Assert.Single(opened.QuerySelectorAll("tbody tr"));
                Assert.Null(opened.QuerySelector("button[disabled]"));
            }
            var fullSnapshot = await service.ReadAsync(actor, token);
            Assert.True(fullSnapshot.CanCreateDifferential);
            var full = Assert.Single(fullSnapshot.Files);
            Assert.True(full.Readable);
            Assert.False(full.CopyOnly);
            await AssertNoPreviousNotificationAsync(service, actor);
            await db.Database.ExecuteSqlRawAsync("UPDATE BackupProbe SET Value = 2;", token);
            await CreateOnPageAsync(service, logger, actor, true, "ClearNotification");
            var snapshot = await service.ReadAsync(actor, token);
            Assert.True(snapshot.CanCreateDifferential);
            var diff = Assert.Single(snapshot.Files, item => item.Differential);
            Assert.Equal(full.Id, diff.BaseId);
            Assert.True(snapshot.Free > 0);
            var preview = await service.PreviewDeleteAsync(actor, full.Name);
            Assert.Equal(2, preview.Length);
            var streams = await service.OpenDownloadAsync(actor, diff.Name, token);
            try { Assert.Equal(2, streams.Length); Assert.All(streams, item => Assert.True(item.Length > 0)); }
            finally { foreach (var stream in streams) await stream.DisposeAsync(); }
            await RestoreAsync(connectionString, sqlPath, full.Name, diff.Name, token);
            await CreateOnPageAsync(service, logger, actor, true, "RefreshAsync");
            await Assert.ThrowsAsync<OperationValidationException>(() => service.DeleteAsync(actor, preview));
            var latest = await service.PreviewDeleteAsync(actor, full.Name);
            Assert.Equal(3, latest.Length);
            await BackupAsync(connectionString, sqlPath + "/unlisted.full", false, token);
            Assert.False((await service.ReadAsync(actor, token)).CanCreateDifferential);
            await service.DeleteAsync(actor, latest);
            var empty = await service.ReadAsync(actor, token);
            Assert.Empty(empty.Files);
            Assert.False(empty.CanCreateDifferential);
            await service.StartAsync(actor, true);
            await WaitAsync(service, token);
            Assert.True(service.State.Failed);
            await AssertNoPreviousNotificationAsync(service, actor);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            Directory.Delete(localPath, true);
        }
    }

    [Fact]
    public async Task LeavingPageCancelsWaitingWithoutCancellingTheBackup()
    {
        var token = TestContext.Current.CancellationToken;
        var actor = Staff((await _factory.AddUserAsync()).Id);
        var environment = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        string directory = Directory.CreateTempSubdirectory("backup-page-test-").FullName;
        using var service = new DatabaseBackupService(Options.Create(new DatabaseBackupOptions
        {
            LocalPath = directory,
            SqlPath = _factory.Services.GetRequiredService<IOptions<DatabaseBackupOptions>>().Value.SqlPath
        }), _factory.Services.GetRequiredService<IDbContextFactory<OlympContext>>(), environment,
            _factory.Services.GetRequiredService<IServiceScopeFactory>(), new BackupLogger());
        var page = new Backups();
        try
        {
            await service.StartAsync(actor, false);
            SetService(page, service);
            var waiting = ActAsync(page, "RefreshAsync");
            Assert.False(waiting.IsCompleted);
            page.Dispose();
            await waiting.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.True(service.State.Running);
        }
        finally { Directory.Delete(directory); }
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

    private sealed class BackupLogger : ILogger<DatabaseBackupService>
    {
        public string? Error { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Error = $"{formatter(state, exception)}\n{exception}";
        }
    }

    private async Task AssertNoPreviousNotificationAsync(DatabaseBackupService service, ClaimsPrincipal actor)
    {
        string html = await RenderAsync<Backups>("/admin/backups", [], actor,
            initialize: page => SetService(page, service));
        using var page = await new HtmlParser().ParseDocumentAsync(html);
        Assert.Null(page.QuerySelector(".alert-success, .alert-danger"));
    }

    private async Task CreateOnPageAsync(DatabaseBackupService service, BackupLogger logger, ClaimsPrincipal actor, bool differential, string? nextAction = null)
    {
        string html = await RenderAsync<Backups>("/admin/backups", [], actor, async page =>
        {
            await ActAsync(page, "CreateAsync", differential);
            if (nextAction is not null) await ActAsync(page, nextAction);
        }, initialize: page => SetService(page, service));
        Assert.False(service.State.Failed, logger.Error ?? service.State.FileName);
        using var document = await new HtmlParser().ParseDocumentAsync(html);
        Assert.Null(document.QuerySelector(".alert-danger"));
        Assert.Equal(nextAction is null, document.QuerySelector(".alert-success .btn-close") is not null);
    }

    private static async Task WaitAsync(DatabaseBackupService service, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        while (service.State.Running) await Task.Delay(25, timeout.Token);
    }

    private static void SetService(Backups page, DatabaseBackupService service) =>
        typeof(Backups).GetProperty("BackupsService", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, service);

    private static async Task BackupAsync(string connectionString, string path, bool copyOnly, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 90;
        string database = connection.Database.Replace("]", "]]", StringComparison.Ordinal);
        command.CommandText = $"BACKUP DATABASE [{database}] TO DISK = @path WITH {(copyOnly ? "COPY_ONLY, " : "")}INIT;";
        command.Parameters.AddWithValue("@path", path);
        await command.ExecuteNonQueryAsync(token);
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

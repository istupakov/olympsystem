using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Checker;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Site.Services.Checker;
using Olymp.Tests.Shared;

using Xunit;

using static Olymp.IntegrationTests.Site.Checkers.CheckerTestData;

namespace Olymp.IntegrationTests.Site.Checkers;

[Trait("Category", "Integration")]
public class CompilatorSynchronizationTests(SiteFactory factory) : IClassFixture<SiteFactory>
{
    [Fact]
    public async Task AdminAddsRunnerCompilerAndRepeatedCommandDoesNotCreateDuplicates()
    {
        var configuration = Configuration();
        var runner = new TestRunner();
        await using var site = WithRunnerConfiguration(factory, configuration);
        var manager = site.Services.GetRequiredService<ICheckerManager>();
        await using var connection = await RunnerRegistration.ConnectAsync(manager, runner);
        Assert.Equal(CheckerSelfTestingStatus.Success, connection.Checker.SelfTestingStatus);
        using var client = await LoginAsync(site, RoleNames.Admin);

        using var first = await AddAsync(client, runner.Id);
        using var second = await AddAsync(client, runner.Id);

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var records = await scope.ServiceProvider.GetRequiredService<OlympContext>().Compilators
            .Where(compiler => compiler.Name == configuration.Name).ToArrayAsync(TestContext.Current.CancellationToken);
        var record = Assert.Single(records);
        Assert.True(record.IsActive);
        Assert.Equal(configuration.DisplayName, record.Description);
        Assert.Equal(configuration.Language, record.Language);
        Assert.Equal(configuration.ConfigName, record.ConfigName);
        Assert.Equal(configuration.SourceExtension, record.SourceExtension);
        Assert.Equal(configuration.CompileCommand, record.CommandLine);
    }

    [Fact]
    public async Task AdminEnablesExistingRecordWithoutChangingItsIdentityOrLegacySettings()
    {
        var configuration = Configuration();
        int id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var record = CreateRecord(configuration, configuration.DisplayName);
            record.IsActive = false;
            record.Language = "Legacy C";
            record.SourceExtension = ".old";
            record.ConfigName = "legacy-env";
            record.CommandLine = "legacy-command";
            db.Compilators.Add(record);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            id = record.Id;
        }
        var runner = new TestRunner();
        await using var site = WithRunnerConfiguration(factory, configuration);
        await using var connection = await RunnerRegistration.ConnectAsync(site.Services.GetRequiredService<ICheckerManager>(), runner);
        using var client = await LoginAsync(site, RoleNames.Admin);

        using var response = await AddAsync(client, runner.Id);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var finalScope = factory.Services.CreateAsyncScope();
        var records = await finalScope.ServiceProvider.GetRequiredService<OlympContext>().Compilators
            .Where(compiler => compiler.Name == configuration.Name).ToArrayAsync(TestContext.Current.CancellationToken);
        var saved = Assert.Single(records);
        Assert.Equal(id, saved.Id);
        Assert.True(saved.IsActive);
        Assert.Equal("Legacy C", saved.Language);
        Assert.Equal(".old", saved.SourceExtension);
        Assert.Equal("legacy-env", saved.ConfigName);
        Assert.Equal("legacy-command", saved.CommandLine);
    }

    [Fact]
    public async Task OldVersionRemainsActiveUntilDisableCommandAndItsSubmissionsArePreserved()
    {
        var configuration = Configuration();
        int oldId;
        int submissionId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var old = CreateRecord(configuration, "Old version");
            db.Compilators.Add(old);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            oldId = old.Id;
            var submission = TestEntities.CreateSubmission(factory.Data.Competitor, factory.Data.Problem, old,
                DateTimeOffset.UtcNow);
            submission.User = null!;
            submission.Problem = null!;
            submission.StatusCode = 2;
            db.Submissions.Add(submission);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            submissionId = submission.Id;
        }
        var runner = new TestRunner();
        await using var site = WithRunnerConfiguration(factory, configuration);
        var manager = site.Services.GetRequiredService<ICheckerManager>();
        await using var connection = await RunnerRegistration.ConnectAsync(manager, runner);
        using var client = await LoginAsync(site, RoleNames.Admin);
        using var added = await AddAsync(client, runner.Id);
        Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<OlympContext>().Compilators
                .Where(compiler => compiler.Id == oldId).Select(compiler => compiler.IsActive)
                .SingleAsync(TestContext.Current.CancellationToken));

        using var disabled = await DisableAsync(client);

        Assert.Equal(HttpStatusCode.Redirect, disabled.StatusCode);
        await using var finalScope = factory.Services.CreateAsyncScope();
        var dbAfter = finalScope.ServiceProvider.GetRequiredService<OlympContext>();
        var records = await dbAfter.Compilators.Where(compiler => compiler.Name == configuration.Name)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, records.Length);
        Assert.False(Assert.Single(records, compiler => compiler.Id == oldId).IsActive);
        Assert.True(Assert.Single(records, compiler => compiler.Description == configuration.DisplayName).IsActive);
        Assert.Equal(oldId, (await dbAfter.Submissions.SingleAsync(submission => submission.Id == submissionId,
            TestContext.Current.CancellationToken)).CompilatorId);
        Assert.Equal(0, await manager.DisableCompilatorsWithoutRunner(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminCannotAddCompilersFromDisconnectedOrFailedRunner(bool failed)
    {
        var configuration = Configuration();
        await using var site = WithRunnerConfiguration(factory, configuration);
        var runner = new TestRunner { VersionStatus = RunnerStatus.UserTimeLimit };
        var manager = site.Services.GetRequiredService<ICheckerManager>();
        await using var connection = failed ? await RunnerRegistration.ConnectAsync(manager, runner) : null;
        using var client = await LoginAsync(site, RoleNames.Admin);

        using var response = await AddAsync(client, runner.Id);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.NotNull(document.QuerySelector(".validation-summary-errors"));
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OlympContext>().Compilators
            .AnyAsync(compiler => compiler.Name == configuration.Name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AdminCanDisableCompilersWhenNoRunnerIsConnected()
    {
        var configuration = Configuration();
        int id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var record = CreateRecord(configuration, configuration.DisplayName);
            db.Compilators.Add(record);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            id = record.Id;
        }
        await using var site = WithRunnerConfiguration(factory, configuration);
        using var client = await LoginAsync(site, RoleNames.Admin);

        using var response = await DisableAsync(client);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var finalScope = factory.Services.CreateAsyncScope();
        var saved = await finalScope.ServiceProvider.GetRequiredService<OlympContext>().Compilators
            .SingleAsync(compiler => compiler.Id == id, TestContext.Current.CancellationToken);
        Assert.False(saved.IsActive);
    }

    [Theory]
    [InlineData("AddCompilators")]
    [InlineData("DisableCompilators")]
    public async Task JuryCannotModifyCompilerAvailability(string handler)
    {
        await using var site = WithRunnerConfiguration(factory);
        using var client = await LoginAsync(site, RoleNames.Jury);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var before = await db.Compilators.OrderBy(compiler => compiler.Id)
            .Select(compiler => new { compiler.Id, compiler.IsActive }).ToArrayAsync(TestContext.Current.CancellationToken);

        using var response = await SiteFactory.PostFormAsync(client, "/Checkers?handler=" + handler,
            new() { ["checkerId"] = Guid.NewGuid().ToString() }, "/Checkers");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("AccessDenied", response.Headers.Location!.ToString());
        var after = await db.Compilators.OrderBy(compiler => compiler.Id)
            .Select(compiler => new { compiler.Id, compiler.IsActive }).ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(before, after);
    }

    private static TestConfiguration Configuration() => new() { Name = "compiler-" + Guid.NewGuid().ToString("N") };

    private static Compilator CreateRecord(TestConfiguration configuration, string description) => new()
    {
        Name = configuration.Name,
        Description = description,
        Language = configuration.Language,
        SourceExtension = configuration.SourceExtension,
        ConfigName = configuration.ConfigName,
        CommandLine = configuration.CompileCommand,
        IsActive = true,
        Submissions = []
    };

    private static Task<HttpResponseMessage> AddAsync(HttpClient client, Guid id) =>
        SiteFactory.PostFormAsync(client, "/Checkers?handler=AddCompilators", new() { ["checkerId"] = id.ToString() }, "/Checkers");

    private static Task<HttpResponseMessage> DisableAsync(HttpClient client) =>
        SiteFactory.PostFormAsync(client, "/Checkers?handler=DisableCompilators", [], "/Checkers");
}

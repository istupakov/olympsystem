using System.Net;

using AngleSharp.Html.Parser;

using Olymp.Checker;
using Olymp.Checker.Checkers;
using Olymp.Checker.Services;
using Olymp.Site;

using Xunit;

using static Olymp.IntegrationTests.Site.Checkers.CheckerTestData;

namespace Olymp.IntegrationTests.Site.Checkers;

[Trait("Category", "Integration")]
public class IndexPageTests(SiteFactory factory) : IClassFixture<SiteFactory>
{
    public enum State { LoadingVersions, Pending, Passed, FailedVersion, NoTests }

    [Theory]
    [InlineData(State.LoadingVersions, "text-bg-secondary")]
    [InlineData(State.Pending, "text-bg-secondary")]
    [InlineData(State.Passed, "text-bg-success")]
    [InlineData(State.FailedVersion, "text-bg-danger")]
    [InlineData(State.NoTests, "text-bg-danger")]
    public async Task CompilerGroupShowsVersionProgramTestsAndTheirCurrentState(State state, string expectedClass)
    {
        var runner = new TestRunner
        {
            VersionStatus = state == State.FailedVersion ? RunnerStatus.UserTimeLimit : RunnerStatus.Completed
        };
        ICheckerTest[] tests = state == State.NoTests ? [] : [new SelfTest()];
        var checker = new CheckerService(runner, [new TestConfiguration()], tests, [new LegacyChecker()]);
        var token = TestContext.Current.CancellationToken;
        if (state == State.Pending)
            await checker.LoadCompilators(token);
        else if (state != State.LoadingVersions)
            await checker.RunSelfTests(token);
        await using var site = WithManager(factory, new PageManager(checker));
        using var client = await LoginAsync(site, RoleNames.Jury);

        using var response = await client.GetAsync("/Checkers", token);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);

        var section = Assert.Single(document.QuerySelectorAll("section"));
        Assert.Contains(runner.Name, section.TextContent);
        Assert.Contains(runner.Id.ToString(), section.TextContent);
        Assert.NotNull(document.QuerySelector("a[href='/checkers/run']"));
        var group = Assert.Single(section.QuerySelectorAll("details.border"));
        Assert.Contains(expectedClass, group.QuerySelector("summary > .badge")!.ClassList);
        var rows = group.QuerySelectorAll("tbody th");
        Assert.Equal(2, rows.Length);
        Assert.Equal("Version", rows[0].TextContent);
        Assert.Equal(state == State.NoTests ? "SelfTests" : nameof(SelfTest), rows[1].TextContent);
        if (state != State.LoadingVersions)
        {
            Assert.Contains("command: version", group.TextContent);
            Assert.Contains("peak memory:", group.TextContent);
        }
        if (state is State.LoadingVersions or State.Pending or State.FailedVersion)
        {
            var pending = rows[1].ParentElement!;
            Assert.DoesNotContain("table-danger", pending.ClassList);
            Assert.True(pending.Children[2].TextContent is "Not run" or "Не выполнялся");
            Assert.Equal(state == State.LoadingVersions ? 0 : 1, group.QuerySelectorAll("tbody pre").Length);
        }
        if (state == State.FailedVersion)
            Assert.Contains(nameof(RunnerStatus.UserTimeLimit), group.TextContent);
    }

    [Fact]
    public async Task NoConnectedRunnerShowsEmptyStateAndKeepsRunLink()
    {
        await using var site = WithManager(factory, new PageManager());
        using var client = await LoginAsync(site, RoleNames.Jury);

        using var response = await client.GetAsync("/Checkers", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.NotNull(document.QuerySelector(".alert-warning"));
        Assert.Empty(document.QuerySelectorAll("section"));
        Assert.NotNull(document.QuerySelector("a[href='/checkers/run']"));
    }

    [Theory]
    [InlineData("/Checkers")]
    [InlineData("/Checkers/Run")]
    public async Task AnonymousUserMustLogin(string path)
    {
        using var client = factory.CreateSiteClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Identity/Account/Login", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("/Checkers")]
    [InlineData("/Checkers/Run")]
    public async Task CompetitorCannotAccessCheckerAdministration(string path)
    {
        using var client = await factory.LoginAsync("team");

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("AccessDenied", response.Headers.Location!.ToString());
    }
}

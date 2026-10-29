using System.Text;

using AngleSharp.Html.Parser;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Checker;
using Olymp.Checker.Services;
using Olymp.Domain;
using Olymp.Site;

using Xunit;

using static Olymp.IntegrationTests.Site.Checkers.CheckerTestData;

namespace Olymp.IntegrationTests.Site.Checkers;

[Trait("Category", "Integration")]
public class RunPageTests(SiteFactory factory) : IClassFixture<SiteFactory>
{
    private const string Source = "// Привет\nsource";
    private const string Input = "  мир  \r\nlast";

    [Theory]
    [InlineData(CheckerSelfTestingStatus.Unknown)]
    [InlineData(CheckerSelfTestingStatus.Success)]
    [InlineData(CheckerSelfTestingStatus.Failed)]
    public async Task ManualRunUsesRunnerConfigurationWithoutDatabaseRecordOrPassedSelfTest(CheckerSelfTestingStatus state)
    {
        var runner = new TestRunner
        {
            VersionStatus = state == CheckerSelfTestingStatus.Failed ? RunnerStatus.UserTimeLimit : RunnerStatus.Completed
        };
        var configuration = new TestConfiguration();
        var checker = new CheckerService(runner, [configuration], [new SelfTest()], []);
        if (state != CheckerSelfTestingStatus.Unknown)
            await checker.RunSelfTests(TestContext.Current.CancellationToken);
        Assert.Equal(state, checker.SelfTestingStatus);
        int previousRequests = runner.Requests.Count;
        await using var site = WithManager(factory, new PageManager(checker));
        using var client = await LoginAsync(site, RoleNames.Jury);
        using (var page = await client.GetAsync("/Checkers/Run", TestContext.Current.CancellationToken))
        {
            page.EnsureSuccessStatusCode();
            using var document = await ParseAsync(page);
            Assert.NotNull(document.QuerySelector($"select[name='Input.CheckerId'] option[value='{runner.Id}'][selected]"));
            Assert.NotNull(document.QuerySelector("select[name='Input.CompilatorName'] option[value='test']"));
        }

        using var response = await SiteFactory.PostFormAsync(client, "/Checkers/Run", Fields(runner));
        response.EnsureSuccessStatusCode();
        using var result = await ParseAsync(response);

        var log = result.QuerySelector("pre samp")!.TextContent;
        Assert.Contains("Compilation:", log);
        Assert.Contains("Execution:", log);
        Assert.Contains("stdout:", log);
        Assert.Equal(Encoding.UTF8.GetBytes(Source), runner.CompilationSource);
        var requests = runner.Requests.Skip(previousRequests).ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(Encoding.UTF8.GetBytes(Input), requests[1].Stdin.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(1), requests[1].ResourceLimits.UserTime);
        Assert.Equal(128ul * 1024 * 1024, requests[1].ResourceLimits.Memory);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False(await db.Compilators.AnyAsync(compiler => compiler.Name == configuration.Name,
            TestContext.Current.CancellationToken));
        Assert.Equal(2, await db.Submissions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("source", "missing", "Input.CompilatorName")]
    [InlineData("", "test", "Input.Code")]
    public async Task InvalidFormShowsFieldErrorAndDoesNotCallRunner(string code, string compiler, string field)
    {
        var runner = new TestRunner();
        var checker = new CheckerService(runner, [new TestConfiguration()], [], []);
        await using var site = WithManager(factory, new PageManager(checker));
        using var client = await LoginAsync(site, RoleNames.Jury);
        var fields = Fields(runner);
        fields["Input.Code"] = code;
        fields["Input.CompilatorName"] = compiler;

        using var response = await SiteFactory.PostFormAsync(client, "/Checkers/Run", fields);
        response.EnsureSuccessStatusCode();
        using var document = await ParseAsync(response);

        Assert.False(string.IsNullOrWhiteSpace(document.QuerySelector($"[data-valmsg-for='{field}']")!.TextContent));
        Assert.Empty(runner.Requests);
        Assert.Null(document.QuerySelector("pre samp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectedRunnerShowsValidationErrorInsteadOfServerError(bool duringExecution)
    {
        var runner = new TestRunner();
        var checker = new CheckerService(runner, [new TestConfiguration()], [], []);
        var manager = new PageManager(checker);
        if (duringExecution)
            runner.BeforeCompile = _ =>
            {
                manager.Unregister(runner);
                throw new InvalidOperationException("Runner disconnected");
            };
        await using var site = WithManager(factory, manager);
        using var client = await LoginAsync(site, RoleNames.Jury);
        if (!duringExecution)
            manager.Unregister(runner);

        using var response = await SiteFactory.PostFormAsync(client, "/Checkers/Run", Fields(runner));
        response.EnsureSuccessStatusCode();
        using var document = await ParseAsync(response);

        Assert.False(string.IsNullOrWhiteSpace(document.QuerySelector("[data-valmsg-for='Input.CheckerId']")!.TextContent));
        Assert.Equal(Source, document.QuerySelector("textarea[name='Input.Code']")!.TextContent);
        Assert.Equal(duringExecution ? 1 : 0, runner.Requests.Count);
        Assert.Null(document.QuerySelector("pre samp"));
    }

    [Fact]
    public async Task CompilationFailureIsDisplayedWithoutExecution()
    {
        var runner = new TestRunner { CompilationFails = true };
        var checker = new CheckerService(runner, [new TestConfiguration()], [], []);
        await using var site = WithManager(factory, new PageManager(checker));
        using var client = await LoginAsync(site, RoleNames.Jury);

        using var response = await SiteFactory.PostFormAsync(client, "/Checkers/Run", Fields(runner));
        response.EnsureSuccessStatusCode();
        using var document = await ParseAsync(response);

        var log = document.QuerySelector("pre samp")!.TextContent;
        Assert.Contains(TestRunner.CompilerError, log.Replace("\r\n", "\n"));
        Assert.DoesNotContain("Execution:", log);
        Assert.Equal(0, runner.TestsRun);
    }

    [Fact]
    public async Task NoRunnerDisablesRunForm()
    {
        await using var site = WithManager(factory, new PageManager());
        using var client = await LoginAsync(site, RoleNames.Jury);

        using var response = await client.GetAsync("/Checkers/Run", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await ParseAsync(response);

        Assert.Equal(2, document.QuerySelectorAll(".alert-warning").Length);
        Assert.NotNull(document.QuerySelector("button.btn-primary[type='submit'][disabled]"));
    }

    private static Dictionary<string, string> Fields(TestRunner runner) => new()
    {
        ["Input.CheckerId"] = runner.Id.ToString(),
        ["Input.CompilatorName"] = "test",
        ["Input.Code"] = Source,
        ["Input.Input"] = Input
    };

    private static async Task<AngleSharp.Html.Dom.IHtmlDocument> ParseAsync(HttpResponseMessage response) =>
        await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
}

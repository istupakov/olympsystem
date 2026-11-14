using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;

using AngleSharp.Html.Parser;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Olymp.Checker;
using Olymp.Checker.Checkers;
using Olymp.Checker.Services;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.Services.Checkers;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Components.Submissions;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Checker;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

[Trait("Category", "Integration")]
public class TrialRunTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckUsesEditedLimitsAndSourceAndDoesNotSaveResults(bool inactiveTest)
    {
        var setup = await SetupAsync();
        var html = await RenderAsync<TrialRun>($"/jury/trial-run?Submission={setup.Submission.Id}{(inactiveTest ? $"&Test={setup.Inactive.Id}" : "")}", [], Staff(setup.Author.Id, RoleNames.Jury), async page =>
        {
            SetInput(page, "Source", "edited source"); SetInput(page, "Seconds", 3.5); SetInput(page, "Memory", 512);
            await ActAsync(page, "RunAsync");
        }, services => { services.RemoveAll<ICheckerManager>(); services.AddSingleton<ICheckerManager>(setup.Manager); });
        Assert.Contains("Compilation:", html);
        Assert.Contains(inactiveTest ? "Test 3" : "Test 2", html);
        Assert.Contains("WrongAnswer", html);
        var runs = setup.Runner.Requests.Where(item => item.Command == "run").Skip(1).ToArray();
        Assert.Equal(inactiveTest ? 1 : 2, runs.Length);
        Assert.All(runs, run => { Assert.Equal(TimeSpan.FromSeconds(3.5), run.ResourceLimits.UserTime); Assert.Equal(512UL * 1024 * 1024, run.ResourceLimits.Memory); });
        Assert.Equal(inactiveTest ? setup.Inactive.Input : setup.First.Input, runs[0].Stdin.ToArray());
        Assert.Equal("edited source", Encoding.UTF8.GetString(setup.Runner.CompilationSource!));
        Assert.Equal(2, setup.Runner.Requests.Count(item => item.Command.StartsWith("compile", StringComparison.Ordinal)));
        await using var scope = _factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<OlympContext>().Submissions.AsNoTracking().Include(item => item.CheckResults)
            .SingleAsync(item => item.Id == setup.Submission.Id, TestContext.Current.CancellationToken);
        Assert.Equal("original source", stored.Text); Assert.Equal(2, stored.StatusCode); Assert.Empty(stored.CheckResults);
    }

    [Theory]
    [InlineData(0, 128)]
    [InlineData(-1, 128)]
    [InlineData(1, 3)]
    [InlineData(1, 1024)]
    public async Task InvalidLimitsNeverReachRunner(double seconds, int memory)
    {
        var setup = await SetupAsync();
        int calls = setup.Runner.Requests.Count;
        var html = await RenderAsync<TrialRun>($"/jury/trial-run?Submission={setup.Submission.Id}", [], Staff(setup.Author.Id), async page =>
        {
            SetInput(page, "Seconds", seconds); SetInput(page, "Memory", memory); await ActAsync(page, "RunAsync");
        }, services => { services.RemoveAll<ICheckerManager>(); services.AddSingleton<ICheckerManager>(setup.Manager); });
        Assert.Contains(seconds <= 0 ? SiteResources.StaffRunTimeInvalid : SiteResources.StaffRunMemoryInvalid, html);
        Assert.Equal(calls, setup.Runner.Requests.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AccessAndTestOwnershipAreRechecked(bool wrongTest)
    {
        var setup = await SetupAsync();
        int calls = setup.Runner.Requests.Count;
        var actor = wrongTest ? Staff(setup.Author.Id) : Staff(setup.Author.Id, "user");
        var html = await RenderAsync<TrialRun>($"/jury/trial-run?Submission={setup.Submission.Id}", [], actor, async page =>
        {
            if (wrongTest) SetInput(page, "Test", int.MaxValue);
            await ActAsync(page, "RunAsync");
        }, services => { services.RemoveAll<ICheckerManager>(); services.AddSingleton<ICheckerManager>(setup.Manager); });
        Assert.Contains(wrongTest ? SiteResources.StaffRunTestNotFound : SiteResources.StaffAccessDenied, html);
        Assert.Equal(calls, setup.Runner.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidTestLinkPreservesSelectionAndNeverRunsAllTests(bool foreignTest)
    {
        var setup = await SetupAsync();
        int testId = foreignTest ? (await SetupAsync()).First.Id : int.MaxValue;
        int calls = setup.Runner.Requests.Count;
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<TrialRun>($"/jury/trial-run?Submission={setup.Submission.Id}&Test={testId}", [], Staff(setup.Author.Id), async page =>
        {
            var input = typeof(TrialRun).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            Assert.Equal(testId, input.GetType().GetProperty("Test")!.GetValue(input));
            await ActAsync(page, "RunAsync");
        }, services => { services.RemoveAll<ICheckerManager>(); services.AddSingleton<ICheckerManager>(setup.Manager); }));

        Assert.Equal(calls, setup.Runner.Requests.Count);
        Assert.Equal(SiteResources.StaffRunTestNotFound, html.QuerySelector("#trial-unavailable")!.TextContent);
        Assert.Contains(html.QuerySelectorAll("button[disabled]"), button => button.TextContent == SiteResources.StaffRun);
        var link = Assert.Single(html.QuerySelectorAll("a"), item => item.TextContent == SiteResources.StaffAllActiveTests);
        Assert.Contains($"Submission={setup.Submission.Id}", link.GetAttribute("href"));
        Assert.DoesNotContain("Test=", link.GetAttribute("href"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticRunNeedsNoProblemAndMayUseRunnerWithFailedSelfTests(bool passed)
    {
        var author = await _factory.AddUserAsync();
        var runner = new CheckerTestData.TestRunner { FailureTests = passed ? [] : [1] };
        var checker = await CheckerTestData.CreateCheckerAsync(runner, "diagnostic");
        Assert.Equal(passed, await checker.RunSelfTests(TestContext.Current.CancellationToken));
        var manager = new CheckerTestData.PageManager(checker);
        int runs = runner.Requests.Count;

        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<TrialRun>($"/jury/trial-run?Runner={runner.Id}&Configuration=diagnostic", [], Staff(author.Id), async page =>
        {
            SetInput(page, "Source", "edited diagnostic source");
            SetInput(page, "Input", "custom stdin");
            SetInput(page, "Seconds", 2.5);
            SetInput(page, "Memory", 64);
            await ActAsync(page, "RunAsync");
        }, services => { services.RemoveAll<ICheckerManager>(); services.AddSingleton<ICheckerManager>(manager); }));

        Assert.Null(html.QuerySelector("#trial-problem"));
        Assert.Null(html.QuerySelector("#trial-compiler"));
        Assert.NotNull(html.QuerySelector("#trial-configuration"));
        Assert.NotNull(html.QuerySelector("#trial-input"));
        Assert.Contains("Execution:", html.Body!.TextContent);
        Assert.Equal(runs + 2, runner.Requests.Count);
        Assert.Equal("custom stdin", Encoding.UTF8.GetString(runner.Requests.Last().Stdin.Span));
        Assert.Equal(TimeSpan.FromSeconds(2.5), runner.Requests.Last().ResourceLimits.UserTime);
        Assert.Equal(64UL * 1024 * 1024, runner.Requests.Last().ResourceLimits.Memory);
    }

    [Fact]
    public async Task InactiveOriginalCompilerIsShownButNeverAutomaticallyReplaced()
    {
        var setup = await SetupAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OlympContext>().Compilators.Where(item => item.Id == setup.Submission.CompilatorId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false), TestContext.Current.CancellationToken);

        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<TrialRun>($"/jury/trial-run?Submission={setup.Submission.Id}", [], Staff(setup.Author.Id),
            configure: services => { services.RemoveAll<ICheckerManager>(); services.AddSingleton<ICheckerManager>(setup.Manager); }));

        Assert.Contains(SiteResources.StaffOriginalCompiler, html.Body!.TextContent);
        Assert.DoesNotContain(html.QuerySelectorAll("#trial-compiler option"), option => option.GetAttribute("value") == setup.Submission.CompilatorId.ToString());
        if (html.QuerySelector("#trial-unavailable") is { } reason) Assert.Equal(SiteResources.NoActiveCompilers, reason.TextContent);
        Assert.NotNull(html.QuerySelector("#trial-compiler option[value='']"));
        Assert.Null(html.QuerySelector("#trial-configuration"));
        Assert.Null(html.QuerySelector("#trial-input"));
    }

    [Fact]
    public async Task TaskRunRejectsCompilerDisabledAfterPageLoad()
    {
        var setup = await SetupAsync();
        int calls = setup.Runner.Requests.Count;
        var html = await RenderAsync<TrialRun>($"/jury/trial-run?Submission={setup.Submission.Id}", [], Staff(setup.Author.Id), async page =>
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OlympContext>().Compilators.Where(item => item.Id == setup.Submission.CompilatorId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false), TestContext.Current.CancellationToken);
            await ActAsync(page, "RunAsync");
        }, services => { services.RemoveAll<ICheckerManager>(); services.AddSingleton<ICheckerManager>(setup.Manager); });

        Assert.Contains(SiteResources.StaffCompilerUnavailable, html);
        Assert.Equal(calls, setup.Runner.Requests.Count);
    }

    [Fact]
    public async Task TaskEntryHasOnlyPassedMatchingRunnersAndNoConfigurationSelector()
    {
        var setup = await SetupAsync();
        var failedRunner = new CheckerTestData.TestRunner { FailureTests = [1] };
        var failed = await CheckerTestData.CreateCheckerAsync(failedRunner, setup.Submission.Compilator.Name);
        Assert.False(await failed.RunSelfTests(TestContext.Current.CancellationToken));
        setup.Manager.Connected.Add(failed.Id, failed);

        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<TrialRun>($"/jury/trial-run?Problem={setup.Submission.ProblemId}", [], Staff(setup.Author.Id),
            configure: services => { services.RemoveAll<ICheckerManager>(); services.AddSingleton<ICheckerManager>(setup.Manager); }));

        Assert.Contains(html.QuerySelectorAll("#trial-runner option"), option => option.GetAttribute("value") == setup.Runner.Id.ToString());
        Assert.DoesNotContain(html.QuerySelectorAll("#trial-runner option"), option => option.GetAttribute("value") == failed.Id.ToString());
        Assert.Null(html.QuerySelector("#trial-configuration"));
        Assert.Null(html.QuerySelector("#trial-problem"));
        Assert.Null(html.QuerySelector("#trial-input"));
    }

    [Fact]
    public async Task MissingRunnerHasVisibleReasonNextToDisabledButton()
    {
        var setup = await SetupAsync();
        setup.Manager.Connected.Clear();
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<TrialRun>($"/jury/trial-run?Submission={setup.Submission.Id}", [], Staff(setup.Author.Id),
            configure: services => { services.RemoveAll<ICheckerManager>(); services.AddSingleton<ICheckerManager>(setup.Manager); }));

        Assert.Equal(SiteResources.StaffNoMatchingRunner, html.QuerySelector("#trial-unavailable")!.TextContent);
        Assert.NotNull(html.QuerySelector("button[disabled][aria-describedby='trial-unavailable']"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(32768, true)]
    [InlineData(32769, false)]
    public async Task TrialAndSubmissionShareSourceValidation(int length, bool valid)
    {
        var setup = await SetupAsync();
        string source = new('a', length);
        var submission = new SubmissionInput { CompilatorId = setup.Submission.CompilatorId, Code = source };
        var errors = new List<ValidationResult>();
        Assert.Equal(valid, Validator.TryValidateObject(submission, new(submission), errors, true));
        int calls = setup.Runner.Requests.Count;

        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<TrialRun>($"/jury/trial-run?Submission={setup.Submission.Id}", [], Staff(setup.Author.Id), async page =>
        {
            SetInput(page, "Source", source);
            await ActAsync(page, "RunAsync");
        }, services => { services.RemoveAll<ICheckerManager>(); services.AddSingleton<ICheckerManager>(setup.Manager); }));

        if (valid) Assert.True(setup.Runner.Requests.Count > calls);
        else
        {
            Assert.Equal(calls, setup.Runner.Requests.Count);
            Assert.Contains(Assert.Single(errors).ErrorMessage!, html.QuerySelector("[role=alert]")!.TextContent);
            Assert.Contains(Assert.Single(errors).ErrorMessage!, html.QuerySelector("#trial-source + div")!.TextContent);
        }
    }

    [Fact]
    public async Task RequiredCompilerAndRunnerErrorsAreAssociatedWithFields()
    {
        var setup = await SetupAsync();
        int calls = setup.Runner.Requests.Count;
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<TrialRun>($"/jury/trial-run?Submission={setup.Submission.Id}", [], Staff(setup.Author.Id), async page =>
        {
            var input = typeof(TrialRun).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            input.GetType().GetProperty("Compiler")!.SetValue(input, null);
            input.GetType().GetProperty("Runner")!.SetValue(input, null);
            await ActAsync(page, "RunAsync");
        }, services => { services.RemoveAll<ICheckerManager>(); services.AddSingleton<ICheckerManager>(setup.Manager); }));

        Assert.Equal(SiteResources.CompilerRequired, html.QuerySelector("#trial-compiler + div")!.TextContent);
        Assert.Equal(SiteResources.StaffSelectRunner, html.QuerySelector("#trial-runner + div")!.TextContent);
        Assert.Equal(calls, setup.Runner.Requests.Count);
    }

    private static void SetInput(TrialRun page, string name, object value)
    {
        var input = typeof(TrialRun).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        input.GetType().GetProperty(name)!.SetValue(input, value);
    }
    private async Task<(OlympUser Author, Submission Submission, Test First, Test Inactive, CheckerTestData.TestRunner Runner, CheckerTestData.PageManager Manager)> SetupAsync()
    {
        var author = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var problem = TestEntities.CreateProblem(TestEntities.CreateContest("Trial", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1)));
        var first = new Test { Number = 1, Problem = problem, IsActive = true, Input = Encoding.UTF8.GetBytes(new string('a', 20000)), Output = "ok"u8.ToArray(), Score = 20 };
        var inactive = new Test { Number = 3, Problem = problem, IsActive = false, Input = "inactive"u8.ToArray(), Output = "ok"u8.ToArray(), Score = 50 };
        problem.Tests.Add(first);
        problem.Tests.Add(new Test { Number = 2, Problem = problem, IsActive = true, Input = "second"u8.ToArray(), Output = "ok"u8.ToArray(), Score = 30 });
        problem.Tests.Add(inactive);
        var config = new CheckerTestData.TestConfiguration();
        var compiler = TestEntities.CreateCompilator();
        compiler.Name = config.Name; compiler.Language = config.Language; compiler.CommandLine = config.CompileCommand;
        compiler.ConfigName = config.ConfigName; compiler.SourceExtension = config.SourceExtension;
        var submission = TestEntities.CreateSubmission(author, problem, compiler, DateTimeOffset.Now, "original source"u8.ToArray());
        submission.User = null!; submission.StatusCode = 2; db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runner = new CheckerTestData.TestRunner { FailureTests = [2] };
        var checker = new CheckerService(runner, [config], [new CheckerTestData.SelfTest()], [new TestChecker(problem.CheckerId)]);
        await checker.LoadCompilators(TestContext.Current.CancellationToken);
        Assert.True(await checker.RunSelfTests(TestContext.Current.CancellationToken));
        return (author, submission, first, inactive, runner, new(checker));
    }
    private sealed class TestChecker(int id) : ISimpleChecker { public int Id => id; public CheckerResultStatus Check(string input, string expected, string actual) => new LegacyChecker().Check(input, expected, actual); }
}

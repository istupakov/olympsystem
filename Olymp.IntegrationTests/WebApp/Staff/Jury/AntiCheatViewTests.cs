using System.ComponentModel.DataAnnotations;
using System.Reflection;

using AngleSharp.Html.Parser;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Olymp.Checker;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

[Trait("Category", "Integration")]
public class AntiCheatViewTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData("Compare", false, false)]
    [InlineData("User", true, false)]
    [InlineData("Ai", true, false)]
    [InlineData("Period", true, true)]
    public void ValidationOnlyUsesFieldsInTheCurrentMode(string mode, bool validatesThreshold, bool validatesDates)
    {
        var type = typeof(AntiCheatTools).GetNestedType("InputModel", BindingFlags.NonPublic)!;
        var input = Activator.CreateInstance(type, nonPublic: true)!;
        void Set(string name, object value) => type.GetProperty(name)!.SetValue(input, value);
        Set("Mode", Enum.Parse(type.GetProperty("Mode")!.PropertyType, mode));
        Set("User", "author"); Set("FirstId", 1); Set("SecondId", 2);
        Set("Threshold", -1f); Set("AiThreshold", -1f);
        Set("From", DateTime.Today.AddDays(1)); Set("To", DateTime.Today);
        var errors = new List<ValidationResult>();
        Validator.TryValidateObject(input, new(input), errors, true);

        Assert.Equal(validatesThreshold, errors.Any(error => error.ErrorMessage == SiteResources.StaffThresholdInvalid));
        Assert.Equal(validatesDates, errors.Any(error => error.ErrorMessage == SiteResources.StaffInvalidPeriod));
        Assert.Equal(validatesThreshold || validatesDates, errors.Count > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyScopeExplainsMissingSubmissionsInsteadOfShowingEmptyTaskSelector(bool selectedAuthor)
    {
        var author = await _factory.AddUserAsync();
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<AntiCheatTools>(selectedAuthor ? $"/jury/anticheat?AuthorId={author.Id}" : "/jury/anticheat", [], Staff(author.Id)));
        Assert.Empty(html.QuerySelectorAll("input[id^=analysis-problem-]"));
        Assert.DoesNotContain("(0/0)", html.Body!.TextContent);
        Assert.Contains(selectedAuthor ? SiteResources.StaffAuthorNoSubmissions : SiteResources.StaffChooseAnalysisAuthor, html.Body.TextContent);
        Assert.True(html.QuerySelectorAll("button").Single(button => button.TextContent == SiteResources.StaffRunAnalysis).HasAttribute("disabled"));
        Assert.Equal(!selectedAuthor, html.QuerySelector("#mode") is not null);
        Assert.Equal(!selectedAuthor, html.QuerySelector("#analysis-user") is not null);
    }

    [Theory]
    [InlineData("submission")]
    [InlineData("contest")]
    [InlineData("selection")]
    public async Task ContextEntryHidesScopeControlsAndRetainsAppliedSelection(string context)
    {
        var (author, submission) = await AddIpSubmissionAsync("203.0.113.7");
        string selection = $"/jury/submissions?Problem={submission.ProblemId}&AuthorId={author.Id}&IpAddress=203.0.113.7";
        string path = context == "submission" ? $"/jury/anticheat?SubmissionId={submission.Id}"
            : context == "selection" ? $"/jury/anticheat?Selection={Uri.EscapeDataString(selection)}" : "/jury/anticheat";
        Dictionary<string, object?> parameters = context == "contest" ? new() { [nameof(AntiCheatTools.ContestId)] = submission.Problem.ContestId } : [];

        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<AntiCheatTools>(path, parameters, Staff(author.Id)));
        Assert.Null(html.QuerySelector("#mode"));
        Assert.Null(html.QuerySelector("#first-id"));
        Assert.Null(html.QuerySelector("#analysis-from"));
        Assert.NotNull(html.QuerySelector("#tools-threshold"));
        Assert.Empty(html.QuerySelectorAll("input[id^=analysis-problem-]"));
        if (context == "selection")
        {
            Assert.Contains("203.0.113.7", html.Body!.TextContent);
            Assert.Contains(html.QuerySelectorAll("a"), link => link.GetAttribute("href") == selection);
        }
        else if (context == "submission")
            Assert.Contains(html.QuerySelectorAll("a"), link => link.GetAttribute("href") == $"jury/submissions/{submission.Id}");
    }

    [Fact]
    public async Task SingleTaskSelectionFiltersCachedReportAndWarnsOnlyForUnanalyzedTasks()
    {
        var author = await _factory.AddUserAsync();
        var other = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Analysis selection", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1));
        var first = TestEntities.CreateProblem(contest, 1);
        var second = TestEntities.CreateProblem(contest, 2);
        var compiler = TestEntities.CreateCompilator();
        foreach (var problem in new[] { first, second })
            foreach (var user in new[] { author, other })
            {
                var submission = TestEntities.CreateSubmission(user, problem, compiler, DateTimeOffset.Now);
                submission.User = null!;
                db.Submissions.Add(submission);
            }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var analyzer = new CountingAnalyzer();
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<AntiCheatTools>($"/jury/anticheat?AuthorId={author.Id}", [], Staff(author.Id), async page =>
        {
            var input = typeof(AntiCheatTools).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            input.GetType().GetProperty("From")!.SetValue(input, DateTime.Today.AddDays(1));
            input.GetType().GetProperty("To")!.SetValue(input, DateTime.Today);
            await ActAsync(page, "LoadProblemsAsync");
            await ActAsync(page, "ToggleProblem", second.Id, false);
            await ActAsync(page, "RunAsync");
            Assert.Equal(1, analyzer.Detections);
            Assert.Equal(1, analyzer.Comparisons);
            var analyzed = (HashSet<int>)typeof(AntiCheatTools).GetField("_analyzedProblems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            Assert.Equal([first.Id], analyzed);
            await ActAsync(page, "ToggleProblem", first.Id, false);
            await ActAsync(page, "ToggleProblem", first.Id, true);
            Assert.False((bool)typeof(AntiCheatTools).GetProperty("NeedsAnalysis", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!);
            await ActAsync(page, "ToggleProblem", second.Id, true);
            Assert.True((bool)typeof(AntiCheatTools).GetProperty("NeedsAnalysis", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!);
            Assert.Equal(1, analyzer.Detections);
        }, services =>
        {
            services.RemoveAll<ISubmissionSimilarityService>(); services.AddSingleton<ISubmissionSimilarityService>(analyzer);
            services.RemoveAll<IAIDetectionService>(); services.AddSingleton<IAIDetectionService>(analyzer);
        }));

        Assert.Equal(2, html.QuerySelectorAll("input[id^=analysis-problem-]").Length);
        Assert.Empty(html.QuerySelectorAll("input[id^=report-problem-]"));
        Assert.Contains(SiteResources.StaffAnalysisNeedsRun, html.Body!.TextContent);
    }

    private sealed class CountingAnalyzer : ISubmissionSimilarityService, IAIDetectionService
    {
        public int Comparisons { get; private set; }
        public int Detections { get; private set; }
        public float CompareSimilarity(string first, string second) { Comparisons++; return 0.9f; }
        public AIDetectionResult DetectAI(string source, string language) { Detections++; return new(0.9, 0, 0, []); }
    }
}

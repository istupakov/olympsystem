using System.Globalization;
using System.Net;
using System.Text;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Checker;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.Site.Contests;
using Olymp.Site;
using Olymp.Site.Services;
using Olymp.Site.Services.AntiCheat;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.Site.AntiCheat;

[Collection("Contest management")]
public class AntiCheatReportServiceTests(SiteFixture site) : IClassFixture<SiteFixture>
{
    private static readonly DateTimeOffset Start = new(2030, 1, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddDays(1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DateReportComparesPeriodWithHistoryRegardlessOfUserOrder(bool higherIdInPeriod)
    {
        var data = await SeedAsync((0, "first", higherIdInPeriod ? Start.AddDays(-1) : Start.AddHours(1)),
            (1, "second", higherIdInPeriod ? Start.AddHours(1) : Start.AddDays(-1)));
        using var scope = site.Services.CreateScope();
        var analysis = new CountingAnalysis();
        var service = new AntiCheatReportService(scope.ServiceProvider.GetRequiredService<OlympContext>(), analysis, analysis);

        var reports = await service.FindPlagiarismByDateAsync(Start, End, 0.5f, TestContext.Current.CancellationToken);

        var report = Assert.Single(reports);
        var pair = Assert.Single(report.Cases);
        Assert.Equal(data.Submissions.Select(x => x.Id).Order(), new[] { pair.Submission1.Id, pair.Submission2.Id }.Order());
        Assert.Equal(data.Users[0].Id, report.Competitor1.Id);
        Assert.Equal(data.Users[1].Id, report.Competitor2.Id);
        Assert.Single(analysis.Comparisons);
    }

    [Fact]
    public async Task DateReportUsesWholeHistoryWithoutComparingHistoryOnlyPairsOrOtherProblems()
    {
        var data = await SeedAsync((0, "old", Start.AddDays(-1)), (1, "period", Start.AddHours(1)),
            (2, "later", End.AddDays(1)), (1, "same user", Start.AddDays(-2)));
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var unrelatedProblem = TestEntities.CreateProblem(data.Contest, 2);
        unrelatedProblem.Contest = null!;
        db.Problems.Add(unrelatedProblem);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await db.Submissions.Where(x => x.Id == data.Submissions[3].Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProblemId, unrelatedProblem.Id), TestContext.Current.CancellationToken);
        var analysis = new CountingAnalysis();
        var service = new AntiCheatReportService(db, analysis, analysis);

        var reports = await service.FindPlagiarismByDateAsync(Start, End, 0.5f, TestContext.Current.CancellationToken);

        Assert.Equal(2, reports.SelectMany(x => x.Cases).Count());
        Assert.Equal(2, analysis.Comparisons.Count);
        Assert.All(analysis.Comparisons, x => Assert.True(x.Source1 == "period" || x.Source2 == "period"));
        Assert.Contains(analysis.Comparisons, x => x.Source1 == "old" || x.Source2 == "old");
        Assert.Contains(analysis.Comparisons, x => x.Source1 == "later" || x.Source2 == "later");
    }

    [Fact]
    public async Task TwoSubmissionsInPeriodAreComparedOnceAndReportEnumerationDoesNotRepeatAnalysis()
    {
        await SeedAsync((0, "one", Start.AddHours(1)), (1, "two", Start.AddHours(2)));
        using var scope = site.Services.CreateScope();
        var analysis = new CountingAnalysis();
        var service = new AntiCheatReportService(scope.ServiceProvider.GetRequiredService<OlympContext>(), analysis, analysis);

        var reports = await service.FindPlagiarismByDateAsync(Start, End, 0.5f, TestContext.Current.CancellationToken);

        Assert.Single(Assert.Single(reports).Cases);
        foreach (var report in reports)
        {
            Assert.Equal(0.75, report.Cases.Max(x => x.Similarity));
            Assert.Single(report.Cases.GroupBy(x => x.Submission1.Problem));
            Assert.Single(report.Cases);
        }
        Assert.Single(analysis.Comparisons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DateBoundariesAreIncludedWhenSelectingSubmissions(bool atEnd)
    {
        await SeedAsync((0, "at boundary", atEnd ? End : Start), (1, "history", Start.AddDays(-1)));
        using var scope = site.Services.CreateScope();
        var analysis = new CountingAnalysis();
        var service = new AntiCheatReportService(scope.ServiceProvider.GetRequiredService<OlympContext>(), analysis, analysis);

        var reports = await service.FindPlagiarismByDateAsync(Start, End, 0.5f, TestContext.Current.CancellationToken);
        Assert.Single(Assert.Single(reports).Cases);
        Assert.Single(analysis.Comparisons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserReportComparesOnlyDifferentUsersAndSupportsLoginOrDisplayName(bool displayName)
    {
        var data = await SeedAsync((0, "other", Start), (1, "selected", Start), (1, "another own", Start));
        using var scope = site.Services.CreateScope();
        var analysis = new CountingAnalysis();
        var service = new AntiCheatReportService(scope.ServiceProvider.GetRequiredService<OlympContext>(), analysis, analysis);
        var name = displayName ? data.Users[1].Name : data.Users[1].UserName!;

        var report = Assert.Single(await service.FindPlagiarismByUserAsync(name, 0.5f, TestContext.Current.CancellationToken));

        Assert.Equal(2, report.Cases.Count);
        Assert.Equal(2, analysis.Comparisons.Count);
        Assert.All(report.Cases, pair => Assert.NotEqual(pair.Submission1.UserId, pair.Submission2.UserId));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => service.FindPlagiarismByUserAsync(
            Guid.NewGuid().ToString("N"), 0.5f, TestContext.Current.CancellationToken));
        Assert.Equal(2, analysis.Comparisons.Count);
    }

    [Theory]
    [InlineData(0.75f, false)]
    [InlineData(0.74f, true)]
    public async Task ContestReportsApplyStrictThresholdAndMaterializeBothKindsOfAnalysis(float threshold, bool included)
    {
        var data = await SeedAsync((0, "// Привет", Start), (1, "// Другой", Start));
        using var scope = site.Services.CreateScope();
        var analysis = new CountingAnalysis();
        var service = new AntiCheatReportService(scope.ServiceProvider.GetRequiredService<OlympContext>(), analysis, analysis);

        var plagiarism = await service.FindContestPlagiarismAsync(data.Contest.Id, threshold, TestContext.Current.CancellationToken);
        var ai = await service.DetectContestAiAsync(data.Contest.Id, threshold, TestContext.Current.CancellationToken);

        Assert.Equal(included ? 1 : 0, plagiarism.Count);
        Assert.Equal(included ? 2 : 0, ai.Count);
        Assert.All(ai, x => Assert.Equal(0.75, Assert.Single(x.Cases).Result.Score));
        Assert.All(ai, x => Assert.Single(x.Cases.GroupBy(c => c.Submission.Problem)));
        Assert.Single(analysis.Comparisons);
        Assert.Equal(2, analysis.Detections.Count);
        Assert.Contains(("// Привет", "C#"), analysis.Detections);
        Assert.Contains(("// Другой", "C#"), analysis.Detections);
    }

    [Fact]
    public async Task ContestAnalysisExcludesParticipantsFromOtherContests()
    {
        var data = await SeedAsync((0, "one", Start), (1, "two", Start));
        var other = await site.AddContestAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Competitors.Where(x => x.Id == data.Users[1].Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ContestId, other.Id), TestContext.Current.CancellationToken);
        var analysis = new CountingAnalysis();
        var service = new AntiCheatReportService(db, analysis, analysis);

        Assert.Empty(await service.FindContestPlagiarismAsync(data.Contest.Id, 0.5f, TestContext.Current.CancellationToken));
        Assert.Equal(data.Users[0].Id, Assert.Single(await service.DetectContestAiAsync(data.Contest.Id, 0.5f,
            TestContext.Current.CancellationToken)).Competitor.Id);
        Assert.Empty(analysis.Comparisons);
        Assert.Single(analysis.Detections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalysisObservesCancellationDuringComputation(bool ai)
    {
        var data = await SeedAsync((0, "one", Start), (1, "two", Start), (2, "three", Start));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var scope = site.Services.CreateScope();
        var analysis = new CountingAnalysis { OnAnalysis = cancellation.Cancel };
        var service = new AntiCheatReportService(scope.ServiceProvider.GetRequiredService<OlympContext>(), analysis, analysis);

        if (ai)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DetectContestAiAsync(data.Contest.Id, 0.5f, cancellation.Token));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FindContestPlagiarismAsync(data.Contest.Id, 0.5f, cancellation.Token));
        Assert.Equal(1, analysis.Comparisons.Count + analysis.Detections.Count);
    }

    [Fact]
    public async Task DateAndUserPagesRenderHistoryMatchesWithoutRepeatingComparisons()
    {
        var data = await SeedAsync((0, "history", Start.AddDays(-1)), (1, "period", Start.AddHours(1)));
        var admin = await site.AddUserAsync(RoleNames.Admin);
        var analysis = new CountingAnalysis();
        using var factory = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<ISubmissionSimilarityService>(analysis)));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        using var login = await SiteFixture.PostAsync(client, "/Identity/Account/Login", new()
        {
            ["Input.Username"] = admin.UserName!,
            ["Input.Password"] = SiteFixture.Password
        });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        foreach (var path in new[] { "/AntiCheat/ByDate", "/AntiCheat/ByUser" })
        {
            analysis.Comparisons.Clear();
            using var response = await SiteFixture.PostAsync(client, path, new()
            {
                ["Input.StartDate"] = Start.LocalDateTime.ToString("s", CultureInfo.InvariantCulture),
                ["Input.StopDate"] = End.LocalDateTime.ToString("s", CultureInfo.InvariantCulture),
                ["Input.UserName"] = data.Users[1].UserName!,
                ["Input.Threshold"] = "0.5"
            });
            response.EnsureSuccessStatusCode();
            using var document = await new HtmlParser().ParseDocumentAsync(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            var report = Assert.Single(document.QuerySelectorAll("#plagiarism-report .accordion-item"));
            Assert.Contains(data.Users[0].Name, report.TextContent);
            Assert.Contains(data.Users[1].Name, report.TextContent);
            Assert.Single(analysis.Comparisons);
        }
    }

    private async Task<ReportData> SeedAsync(params (int User, string Code, DateTimeOffset Time)[] sources)
    {
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        // Global reports use the entire history of this fixture's temporary database.
        await db.Submissions.ExecuteDeleteAsync(token);
        var problem = TestEntities.CreateProblem(contest);
        problem.Contest = null!;
        var compiler = TestEntities.CreateCompilator();
        db.Problems.Add(problem);
        db.Compilators.Add(compiler);
        await db.SaveChangesAsync(token);

        var users = new List<Competitor>();
        for (int i = 0; i <= sources.Max(x => x.User); i++)
        {
            var user = TestEntities.CreateCompetitor(contest, "Participant " + Guid.NewGuid().ToString("N"));
            user.Contest = null!;
            user.UserName = Guid.NewGuid().ToString("N");
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(user)).Succeeded);
            users.Add(user);
        }

        var submissions = sources.Select(x => TestEntities.CreateSubmission(users[x.User], problem, compiler,
            x.Time, Encoding.GetEncoding(1251).GetBytes(x.Code))).ToArray();
        db.Submissions.AddRange(submissions);
        await db.SaveChangesAsync(token);
        return new ReportData(contest, [.. users], submissions);
    }

    private sealed record ReportData(Contest Contest, Competitor[] Users, Submission[] Submissions);

    private sealed class CountingAnalysis : ISubmissionSimilarityService, IAIDetectionService
    {
        public List<(string Source1, string Source2)> Comparisons { get; } = [];
        public List<(string Source, string Language)> Detections { get; } = [];
        public Action? OnAnalysis { get; init; }

        public float CompareSimilarity(string source1, string source2)
        {
            Comparisons.Add((source1, source2));
            OnAnalysis?.Invoke();
            return 0.75f;
        }

        public AIDetectionResult DetectAI(string source, string language)
        {
            Detections.Add((source, language));
            OnAnalysis?.Invoke();
            return new AIDetectionResult(0.75, 1, 0, ["fragment"]);
        }
    }
}

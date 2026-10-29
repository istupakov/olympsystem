using System.Net;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Site;
using Olymp.Site.Services.Checker;

using Xunit;

using static Olymp.IntegrationTests.Site.Checkers.CheckerTestData;

namespace Olymp.IntegrationTests.Site.Checkers;

[Trait("Category", "Integration")]
public class SubmissionCheckingTests(SiteFactory factory) : IClassFixture<SiteFactory>
{
    [Fact]
    public async Task LegacyCyrillicSourceIsCompiledAndDownloadedAsUtf8()
    {
        const string source = "// Русский комментарий: ёж\nint main() { const char *text = \"Привет, мир!\"; return 0; }";
        var submissionId = await AddSubmissionAsync("school");
        var legacyBytes = Encoding.GetEncoding(1251).GetBytes(source);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Submissions.Where(x => x.Id == submissionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceCode, legacyBytes), TestContext.Current.CancellationToken);
        }

        var runner = new TestRunner();
        await CheckAsync(runner, submissionId);
        var utf8Bytes = Encoding.UTF8.GetBytes(source);
        Assert.Equal(utf8Bytes, runner.CompilationSource);

        using var client = await factory.LoginAsync("team");
        Assert.Contains(source, WebUtility.HtmlDecode(await client.GetStringAsync($"/Submissions/Code/{submissionId}", TestContext.Current.CancellationToken)));
        using var download = await client.GetAsync($"/Submissions/Download/{submissionId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("utf-8", download.Content.Headers.ContentType!.CharSet);
        Assert.Equal(utf8Bytes, await download.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        using var savedScope = factory.Services.CreateScope();
        var saved = await savedScope.ServiceProvider.GetRequiredService<OlympContext>()
            .Submissions.SingleAsync(x => x.Id == submissionId, TestContext.Current.CancellationToken);
        Assert.Equal(legacyBytes, saved.SourceCode);
        Assert.Equal(source, saved.Text);
    }

    [Fact]
    public async Task SchoolCheckSavesScoresAndHistory()
    {
        var submissionId = await AddSubmissionAsync("school");
        var runner = new TestRunner { FailureTests = [1, 3] };
        await CheckAsync(runner, submissionId);
        Assert.Equal(4, runner.TestsRun);
        using var scope = factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<OlympContext>().Submissions
            .Include(x => x.CheckResults).SingleAsync(x => x.Id == submissionId, TestContext.Current.CancellationToken);
        Assert.Equal(-1001, saved.StatusCode);
        Assert.Equal(70, saved.Score);
        Assert.Equal("WrongAnswer;20;WrongAnswer;50", saved.ScoresByTest);
        Assert.Equal(2, saved.CheckResults.Count);
        var result = saved.CheckResults.OrderBy(x => x.CheckTime).Last();
        Assert.Equal(saved.StatusCode, result.StatusCode);
        Assert.Equal(saved.Score, result.Score);
        Assert.Equal(saved.ScoresByTest, result.TestResults);
        Assert.Contains("Old check", saved.CheckResults.Select(x => x.Log));
    }

    [Fact]
    public async Task CompilationErrorsShowDiagnosticsToOwnerAndKeepFullLogForJury()
    {
        var submissionId = await AddSubmissionAsync("school");
        var runner = new TestRunner { CompilationFails = true };
        await CheckAsync(runner, submissionId);
        Assert.Equal(0, runner.TestsRun);
        using var ownerClient = await factory.LoginAsync("team");
        var html = WebUtility.HtmlDecode(await ownerClient.GetStringAsync($"/Submissions/Errors/{submissionId}", TestContext.Current.CancellationToken));
        Assert.Contains(TestRunner.CompilerError, html);
        Assert.DoesNotContain("Runner:", html);
        Assert.DoesNotContain("private compiler version", html);
        Assert.DoesNotContain("user time:", html);
        Assert.DoesNotContain("total time:", html);
        Assert.Contains("AccessDenied", (await ownerClient.GetAsync($"/Submissions/Log/{submissionId}", TestContext.Current.CancellationToken)).Headers.Location!.ToString());
        using var juryClient = await factory.LoginAsync(RoleNames.Jury);
        var logHtml = WebUtility.HtmlDecode(await juryClient.GetStringAsync($"/Submissions/Log/{submissionId}", TestContext.Current.CancellationToken));
        Assert.Contains("Runner:", logHtml);
        Assert.Contains("private compiler version", logHtml);
        Assert.Contains(TestRunner.CompilerError, logHtml);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var result = await db.CheckResults.SingleAsync(x => x.SubmissionId == submissionId && x.StatusCode == -1, TestContext.Current.CancellationToken);
        foreach (var log in new[]
        {
            result.Log.Replace("\n", "\r\n"),
            "Compilation:\r\nuser time: 00:00:00\r\ntotal time: 00:00:00\r\n" + TestRunner.CompilerError,
            TestRunner.CompilerError
        })
        {
            await db.CheckResults.Where(x => x.Id == result.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Log, log), TestContext.Current.CancellationToken);
            html = WebUtility.HtmlDecode(await ownerClient.GetStringAsync($"/Submissions/Errors/{submissionId}", TestContext.Current.CancellationToken));
            Assert.Contains(TestRunner.CompilerError, html.Replace("\r\n", "\n"));
            Assert.DoesNotContain("Runner:", html);
            Assert.DoesNotContain("total time:", html);
        }
    }

    [Theory]
    [InlineData("school", 0, 4, 2, 100, "0;20;30;50")]
    [InlineData("acm", 0, 4, 2, 100, "0;20;30;50")]
    [InlineData("acm", 3, 3, -1003, 20, "0;20;WrongAnswer")]
    public async Task CheckUsesContestModeAndSavesResultWithHistory(string prefix, int failure,
        int expectedRuns, int expectedStatus, int expectedScore, string expectedTestResults)
    {
        int id = await AddSubmissionAsync(prefix);
        var runner = new TestRunner { FailureTests = [failure] };

        await CheckAsync(runner, id);

        Assert.Equal(expectedRuns, runner.TestsRun);
        await using var scope = factory.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<OlympContext>().Submissions
            .Include(submission => submission.CheckResults).SingleAsync(submission => submission.Id == id,
                TestContext.Current.CancellationToken);
        Assert.Equal(expectedStatus, saved.StatusCode);
        Assert.Equal(expectedScore, saved.Score);
        Assert.Equal(expectedTestResults, saved.ScoresByTest);
        Assert.Equal(2, saved.CheckResults.Count);
        var latest = Assert.Single(saved.CheckResults, result => result.Log != "Old check");
        Assert.Equal(saved.StatusCode, latest.StatusCode);
        Assert.Equal(saved.Score, latest.Score);
        Assert.Equal(saved.ScoresByTest, latest.TestResults);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 7)]
    public async Task CheckUsesProblemTimeLimitForSelectedCompiler(bool slow, int expectedSeconds)
    {
        var submission = Assert.Single(await AddSubmissionsAsync(factory, "school", [0]));
        var token = TestContext.Current.CancellationToken;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Problems.Where(problem => problem.Id == submission.ProblemId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(problem => problem.TimeLimit, 1)
                    .SetProperty(problem => problem.SlowTimeLimit, 7), token);
        }
        var runner = new TestRunner();
        var checker = await CreateCheckerAsync(runner, submission.Compilator.Name, slow);

        Assert.True(await factory.Services.GetRequiredService<ICheckerManager>().TryCheckFromDB(checker, token));

        Assert.Equal(4, runner.TestsRun);
        Assert.All(runner.Requests.Where(request => request.Command == "run"), request =>
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), request.ResourceLimits.UserTime));
    }

    [Fact]
    public async Task OnlyActiveTestsAreRunInNumberOrderAndKeepTheirOriginalNumbers()
    {
        var submission = Assert.Single(await AddSubmissionsAsync(factory, "school", [0]));
        var token = TestContext.Current.CancellationToken;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Tests.Where(test => test.ProblemId == submission.ProblemId && test.Number == 2)
                .ExecuteUpdateAsync(setters => setters.SetProperty(test => test.IsActive, false), token);
        }
        var runner = new TestRunner { FailureTests = [2] };

        await CheckAsync(runner, submission.Id);

        Assert.Equal(["1", "3", "4"], runner.Requests.Where(request => request.Command == "run")
            .Select(request => Encoding.UTF8.GetString(request.Stdin.Span)));
        await using var finalScope = factory.Services.CreateAsyncScope();
        var saved = await finalScope.ServiceProvider.GetRequiredService<OlympContext>().Submissions
            .SingleAsync(item => item.Id == submission.Id, token);
        Assert.Equal(-1003, saved.StatusCode);
        Assert.Equal(50, saved.Score);
        Assert.Equal("0;WrongAnswer;50", saved.ScoresByTest);
    }

    private async Task<int> AddSubmissionAsync(string prefix)
    {
        var submissions = await CheckerTestData.AddSubmissionsAsync(factory, prefix, [0]);
        return submissions.Single().Id;
    }

    private async Task CheckAsync(TestRunner runner, int submissionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var name = await scope.ServiceProvider.GetRequiredService<OlympContext>().Submissions
            .Where(submission => submission.Id == submissionId).Select(submission => submission.Compilator.Name)
            .SingleAsync(TestContext.Current.CancellationToken);
        var checker = await CreateCheckerAsync(runner, name);
        Assert.True(await factory.Services.GetRequiredService<ICheckerManager>().TryCheckFromDB(checker, TestContext.Current.CancellationToken));
    }

}

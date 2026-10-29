using System.Net;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Checker;
using Olymp.Checker.Checkers;
using Olymp.Checker.Services;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Site.Services.Checker;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.Site.Contests;

[Collection("Contest management")]
public class CheckerTests(SiteFixture site) : IClassFixture<SiteFixture>
{
    [Fact]
    public async Task LegacyCyrillicSourceIsCompiledAndDownloadedAsUtf8()
    {
        const string source = "// Русский комментарий: ёж\nint main() { const char *text = \"Привет, мир!\"; return 0; }";
        var owner = await site.AddUserAsync();
        var submissionId = await AddSubmissionAsync(owner, "school");
        var legacyBytes = Encoding.GetEncoding(1251).GetBytes(source);
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Submissions.Where(x => x.Id == submissionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceCode, legacyBytes), cancellationToken: TestContext.Current.CancellationToken);
        }

        var runner = new TestRunner();
        await CheckAsync(runner);
        var utf8Bytes = Encoding.UTF8.GetBytes(source);
        Assert.Equal(utf8Bytes, runner.CompilationSource);

        using var client = await site.LoginAsync(owner);
        Assert.Contains(source, WebUtility.HtmlDecode(await client.GetStringAsync($"/Submissions/Code/{submissionId}", TestContext.Current.CancellationToken)));
        var download = await client.GetAsync($"/Submissions/Download/{submissionId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("utf-8", download.Content.Headers.ContentType!.CharSet);
        Assert.Equal(utf8Bytes, await download.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        using var savedScope = site.Services.CreateScope();
        var saved = await savedScope.ServiceProvider.GetRequiredService<OlympContext>()
            .Submissions.SingleAsync(x => x.Id == submissionId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(legacyBytes, saved.SourceCode);
        Assert.Equal(source, saved.Text);
    }

    [Theory]
    [InlineData("school", 0, 100, 4, "0;20;30;50")]
    [InlineData("school", 1, 100, 4, "WrongAnswer;20;30;50")]
    [InlineData("school", 3, 70, 4, "0;20;WrongAnswer;50")]
    [InlineData("school", 4, 50, 4, "0;20;30;WrongAnswer")]
    [InlineData("acm", 3, 20, 3, "0;20;WrongAnswer")]
    public async Task SchoolChecksAllTestsAndOtherModesStopAtFirstFailure(string prefix, int failureTest,
        int? expectedScore, int expectedRuns, string? expectedResults)
    {
        var owner = await site.AddUserAsync();
        var submissionId = await AddSubmissionAsync(owner, prefix);
        var runner = new TestRunner { FailureTests = [failureTest] };
        await CheckAsync(runner);
        Assert.Equal(expectedRuns, runner.TestsRun);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var saved = await db.Submissions.Include(x => x.CheckResults).SingleAsync(x => x.Id == submissionId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expectedScore, saved.Score);
        Assert.Equal(expectedResults, saved.ScoresByTest);
        Assert.Equal(failureTest == 0 ? 2 : -1000 - failureTest, saved.StatusCode);
        var result = saved.CheckResults.OrderBy(x => x.CheckTime).Last();
        Assert.Equal(expectedScore, result.Score);
        Assert.Equal(expectedResults, result.TestResults);
        if (expectedRuns < 4)
            Assert.DoesNotContain($"Test {expectedRuns + 1}", result.Log);
    }

    [Fact]
    public async Task SchoolKeepsFirstFailureAndScoresSuccessesAfterFailures()
    {
        var owner = await site.AddUserAsync();
        var submissionId = await AddSubmissionAsync(owner, "school");
        var runner = new TestRunner { FailureTests = [1, 3] };
        await CheckAsync(runner);
        Assert.Equal(4, runner.TestsRun);
        using var scope = site.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<OlympContext>().Submissions.SingleAsync(x => x.Id == submissionId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(-1001, saved.StatusCode);
        Assert.Equal(70, saved.Score);
        Assert.Equal("WrongAnswer;20;WrongAnswer;50", saved.ScoresByTest);
    }

    [Theory]
    [InlineData(RunnerStatus.UserTimeLimit, 2, 2, -2001)]
    [InlineData(RunnerStatus.MemoryLimit, 2, 2, -6001)]
    [InlineData(RunnerStatus.UserTimeLimit, 1, 5, 2)]
    [InlineData(RunnerStatus.MemoryLimit, 1, 5, 2)]
    public async Task ResourceFailuresGetAtMostTwoAttempts(RunnerStatus failureStatus, int failuresBeforeSuccess,
        int expectedRuns, int expectedStatus)
    {
        var owner = await site.AddUserAsync();
        var submissionId = await AddSubmissionAsync(owner, "acm");
        var runner = new TestRunner { FailureStatus = failureStatus, FailuresBeforeSuccess = failuresBeforeSuccess };
        await CheckAsync(runner);
        Assert.Equal(expectedRuns, runner.TestsRun);
        using var scope = site.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<OlympContext>().Submissions.SingleAsync(x => x.Id == submissionId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expectedStatus, saved.StatusCode);
    }

    [Fact]
    public async Task CompilationErrorsShowDiagnosticsToOwnerAndKeepFullLogForJury()
    {
        var owner = await site.AddUserAsync();
        var submissionId = await AddSubmissionAsync(owner, "school");
        var runner = new TestRunner { CompilationFails = true };
        await CheckAsync(runner);
        Assert.Equal(0, runner.TestsRun);
        using var ownerClient = await site.LoginAsync(owner);
        var html = WebUtility.HtmlDecode(await ownerClient.GetStringAsync($"/Submissions/Errors/{submissionId}", TestContext.Current.CancellationToken));
        Assert.Contains(TestRunner.CompilerError, html);
        Assert.DoesNotContain("Runner:", html);
        Assert.DoesNotContain("private compiler version", html);
        Assert.DoesNotContain("user time:", html);
        Assert.DoesNotContain("total time:", html);
        Assert.Contains("AccessDenied", (await ownerClient.GetAsync($"/Submissions/Log/{submissionId}", TestContext.Current.CancellationToken)).Headers.Location!.ToString());
        var jury = await site.AddUserAsync(RoleNames.Jury);
        using var juryClient = await site.LoginAsync(jury);
        var logHtml = WebUtility.HtmlDecode(await juryClient.GetStringAsync($"/Submissions/Log/{submissionId}", TestContext.Current.CancellationToken));
        Assert.Contains("Runner:", logHtml);
        Assert.Contains("private compiler version", logHtml);
        Assert.Contains(TestRunner.CompilerError, logHtml);

        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var result = await db.CheckResults.SingleAsync(x => x.SubmissionId == submissionId && x.StatusCode == -1, cancellationToken: TestContext.Current.CancellationToken);
        foreach (var log in new[]
        {
            result.Log.Replace("\n", "\r\n"),
            "Compilation:\r\nuser time: 00:00:00\r\ntotal time: 00:00:00\r\n" + TestRunner.CompilerError,
            TestRunner.CompilerError
        })
        {
            await db.CheckResults.Where(x => x.Id == result.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Log, log), cancellationToken: TestContext.Current.CancellationToken);
            html = WebUtility.HtmlDecode(await ownerClient.GetStringAsync($"/Submissions/Errors/{submissionId}", TestContext.Current.CancellationToken));
            Assert.Contains(TestRunner.CompilerError, html.Replace("\r\n", "\n"));
            Assert.DoesNotContain("Runner:", html);
            Assert.DoesNotContain("total time:", html);
        }
    }

    private async Task<int> AddSubmissionAsync(OlympUser owner, string prefix)
    {
        var contest = await site.AddContestAsync(prefix: prefix);
        var files = new List<(string, byte[])>
        {
            ("A/problem.json", """{"number":1,"name":"Scored task","scores":{"1":0,"2":20,"3":30,"4":50}}"""u8.ToArray())
        };
        for (var i = 1; i <= 4; i++)
        {
            files.Add(($"A/input{i}.txt", Encoding.UTF8.GetBytes(i.ToString())));
            files.Add(($"A/output{i}.txt", "ok"u8.ToArray()));
        }
        using var scope = site.Services.CreateScope();
        await ContestManagementTests.ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.Zip([.. files]));
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var problem = await db.Problems.SingleAsync(x => x.ContestId == contest.Id);
        await ContestManagementTests.AddSubmissionsAsync(db, owner.Id, problem, [0]);
        return await db.Submissions.Where(x => x.ProblemId == problem.Id).Select(x => x.Id).SingleAsync();
    }

    private async Task CheckAsync(TestRunner runner)
    {
        var checker = new CheckerService(runner,
            [new TestConfiguration()], [], [new DefaultStrictChecker()]);
        await checker.LoadCompilators(CancellationToken.None);
        Assert.True(await site.Services.GetRequiredService<ICheckerManager>().TryCheckFromDB(checker, CancellationToken.None));
    }

    private sealed class TestConfiguration : ICompilatorConfiguration
    {
        public string Name => "test";
        public string Language => "C";
        public string ConfigName => "test";
        public string SourceExtension => ".c";
        public string CompileCommand => "compile";
        public bool UseSlowTimeLimit => false;
        public int MemoryOverheadMb => 0;
        public string GetSourceFileName(string name) => name + ".c";
        public string GetCompileCommand(string name) => "compile";
        public string RunCommand(string name, int memoryMb) => "run";
        public string GetVersionCommand() => "version";
        public CompilatorInfo ParseInfo(string output) => new(this, "Test compiler", "private compiler version");
    }

    private sealed class TestRunner : IRunnerService
    {
        public const string CompilerError = "42.c:1: error: expected ';'\n42.c:2: error: unknown type 'Compilation:'";
        public int[] FailureTests { get; init; } = [];
        public RunnerStatus FailureStatus { get; init; } = RunnerStatus.Completed;
        public int FailuresBeforeSuccess { get; init; }
        public bool CompilationFails { get; init; }
        public byte[]? CompilationSource { get; private set; }
        public int TestsRun { get; private set; }
        public Guid Id { get; } = Guid.NewGuid();
        public string Name => "Internal runner";
        public DateTimeOffset ConnectingTime => DateTimeOffset.Now;
        public IReadOnlySet<string> SupportedEnvs { get; } = new HashSet<string> { "test" };

        public Task<RunnerResult> Run(RunnerTask request, CancellationToken token = default)
        {
            var compile = request.Command.StartsWith("compile", StringComparison.Ordinal);
            if (compile)
                CompilationSource = request.Files.Single().Content.ToArray();
            var run = request.Command == "run";
            var output = run ? (FailureTests.Contains(++TestsRun) ? "wrong" : "ok") : "";
            var status = compile && CompilationFails ? RunnerStatus.Error
                : run && TestsRun <= FailuresBeforeSuccess ? FailureStatus : RunnerStatus.Completed;
            return Task.FromResult(new RunnerResult(status,
                compile && CompilationFails ? 1 : 0, Encoding.UTF8.GetBytes(output),
                compile && CompilationFails ? Encoding.UTF8.GetBytes(CompilerError) : ReadOnlyMemory<byte>.Empty,
                new(TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0)));
        }
    }
}

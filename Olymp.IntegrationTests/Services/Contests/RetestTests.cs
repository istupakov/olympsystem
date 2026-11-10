using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Checker;
using Olymp.Checker.Checkers;
using Olymp.Checker.Services;
using Olymp.Domain;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Services.Checker;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

[Collection("Contest management")]
public class RetestTests(ContestServiceFixture site) : IClassFixture<ContestServiceFixture>
{
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 2)]
    public async Task RetestModesPreserveHistoryAndQueuedSubmissions(bool exceptAccepted, int expected)
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await ContestMaterialsTests.ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.ProblemZip());
        var problem = await db.Problems.SingleAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken);
        await ContestMaterialsTests.AddSubmissionsAsync(db, owner.Id, problem, [2, -1001, -1, 0]);
        var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
        Assert.Equal(expected, await service.RetestAsync(contest.Id, problem.Id, exceptAccepted, TestContext.Current.CancellationToken));
        db.ChangeTracker.Clear();
        var submissions = await db.Submissions.Where(x => x.ProblemId == problem.Id).Include(x => x.CheckResults).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, submissions.Sum(x => x.CheckResults.Count));
        Assert.Equal(exceptAccepted ? 1 : 0, submissions.Count(x => x.StatusCode == 2));
        Assert.All(submissions.Where(x => x.CheckResults.Single().StatusCode is -1001 or -1), x =>
        {
            Assert.Equal(0, x.StatusCode);
            Assert.Null(x.Score);
            Assert.Null(x.ScoresByTest);
        });
    }

    [Fact]
    public async Task BusyTaskRejectsRetestsAndWholeImportWithoutChanges()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await ContestMaterialsTests.ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.ProblemZip());
        var problem = await db.Problems.SingleAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken);
        await ContestMaterialsTests.AddSubmissionsAsync(db, owner.Id, problem, [2, 1, -1]);
        var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
        foreach (var exceptAccepted in new[] { false, true })
            Assert.Equal(ContestProblemService.BusyMessage,
                (await Assert.ThrowsAsync<InvalidDataException>(() => service.RetestAsync(contest.Id, problem.Id, exceptAccepted, TestContext.Current.CancellationToken))).Message);
        Assert.Equal(ContestProblemService.BusyMessage,
            (await Assert.ThrowsAsync<InvalidDataException>(() => service.RetestCompilationErrorsAsync(contest.Id, TestContext.Current.CancellationToken))).Message);
        await Assert.ThrowsAsync<InvalidDataException>(() => ContestMaterialsTests.ImportAsync(scope.ServiceProvider,
            contest.Id, ProblemArchives.ProblemZip(name: "Не сохранять")));
        db.ChangeTracker.Clear();
        Assert.Equal("Задача", (await db.Problems.SingleAsync(x => x.Id == problem.Id, cancellationToken: TestContext.Current.CancellationToken)).Name);
        Assert.Equal(new[] { 2, 1, -1 }, await db.Submissions.Where(x => x.ProblemId == problem.Id).OrderBy(x => x.Id).Select(x => x.StatusCode).ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentRunnersReleaseGateAndRejectRetestAndImport()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        int problemId;
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            await ContestMaterialsTests.ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.ProblemZip());
            var problem = await db.Problems.SingleAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken);
            problemId = problem.Id;
            await ContestMaterialsTests.AddSubmissionsAsync(db, owner.Id, problem, [0, 0]);
            // Other tests deliberately keep queued submissions. This runner should see only this task.
            await db.Submissions.Where(x => x.ProblemId != problemId).ExecuteUpdateAsync(s => s.SetProperty(x => x.StatusCode, 2), cancellationToken: TestContext.Current.CancellationToken);
        }
        var runner = new BlockingRunner();
        var otherRunner = new BlockingRunner();
        var manager = site.Services.GetRequiredService<ICheckerManager>();
        var checker = new CheckerService(runner,
            [new TestConfiguration()], [], [new DefaultStrictChecker()]);
        var otherChecker = new CheckerService(otherRunner,
            [new TestConfiguration()], [], [new DefaultStrictChecker()]);
        await checker.LoadCompilators(CancellationToken.None);
        await otherChecker.LoadCompilators(CancellationToken.None);
        var check = manager.TryCheckFromDB(checker, CancellationToken.None);
        var otherCheck = manager.TryCheckFromDB(otherChecker, CancellationToken.None);
        try
        {
            await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await otherRunner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(await manager.TryCheckFromDB(checker, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            using var scope = site.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            Assert.Equal(2, await db.Submissions.CountAsync(x => x.ProblemId == problemId && x.StatusCode == 1, cancellationToken: TestContext.Current.CancellationToken));
            var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
            // The gate must be free during compilation, so this fails promptly instead of waiting for the runner.
            await Assert.ThrowsAsync<InvalidDataException>(() => service.RetestAsync(contest.Id, problemId, false, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(() => ContestMaterialsTests.ImportAsync(scope.ServiceProvider,
                contest.Id, ProblemArchives.ProblemZip(name: "Не сохранять")).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        }
        finally
        {
            runner.Release.TrySetResult();
            otherRunner.Release.TrySetResult();
            await Task.WhenAll(check, otherCheck).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        using (var scope = site.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
            Assert.Equal(2, await service.RetestAsync(contest.Id, problemId, false, TestContext.Current.CancellationToken));
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            Assert.Equal(4, await db.CheckResults.CountAsync(x => x.Submission.ProblemId == problemId, cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledCheckReturnsSubmissionToQueue(bool cancel)
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        int problemId;
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            await ContestMaterialsTests.ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.ProblemZip());
            var problem = await db.Problems.SingleAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken);
            problemId = problem.Id;
            await ContestMaterialsTests.AddSubmissionsAsync(db, owner.Id, problem, [0]);
            await db.Submissions.Where(x => x.ProblemId != problemId).ExecuteUpdateAsync(s => s.SetProperty(x => x.StatusCode, 2), cancellationToken: TestContext.Current.CancellationToken);
        }
        var manager = site.Services.GetRequiredService<ICheckerManager>();
        var runner = new BlockingRunner { Fail = !cancel };
        var checker = new CheckerService(runner,
            [new TestConfiguration()], [], [new DefaultStrictChecker()]);
        await checker.LoadCompilators(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var check = manager.TryCheckFromDB(checker, cancellation.Token);
        try
        {
            await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }
            else
            {
                runner.Release.TrySetResult();
                await Assert.ThrowsAsync<InvalidOperationException>(() => check.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            runner.Release.TrySetResult();
        }
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            Assert.Equal(0, await db.Submissions.Where(x => x.ProblemId == problemId).Select(x => x.StatusCode).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.CheckResults.CountAsync(x => x.Submission.ProblemId == problemId, cancellationToken: TestContext.Current.CancellationToken));
        }
        runner.Fail = false;
        Assert.True(await manager.TryCheckFromDB(checker, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        using var finalScope = site.Services.CreateScope();
        var saved = await finalScope.ServiceProvider.GetRequiredService<OlympContext>().Submissions
            .Include(x => x.CheckResults).SingleAsync(x => x.ProblemId == problemId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(0, saved.StatusCode);
        Assert.NotEqual(1, saved.StatusCode);
        Assert.Equal(2, saved.CheckResults.Count);
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
        public CompilatorInfo ParseInfo(string output) => new(this, "Test compiler", "test version");
    }

    private sealed class BlockingRunner : IRunnerService
    {
        public bool Fail { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid Id { get; } = Guid.NewGuid();
        public string Name => "Test runner";
        public DateTimeOffset ConnectingTime => DateTimeOffset.Now;
        public IReadOnlySet<string> SupportedEnvs { get; } = new HashSet<string> { "test" };
        public async Task<RunnerResult> Run(RunnerTask request, CancellationToken token = default)
        {
            if (request.Command.StartsWith("compile", StringComparison.Ordinal))
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(token);
                if (Fail)
                    throw new InvalidOperationException("Injected runner failure");
            }
            return new(RunnerStatus.Completed, 0, new byte[] { 1, 2 }, ReadOnlyMemory<byte>.Empty,
                new(TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0));
        }
    }
}

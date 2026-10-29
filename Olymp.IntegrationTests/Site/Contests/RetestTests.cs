using System.Net;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

using Olymp.Checker;
using Olymp.Checker.Checkers;
using Olymp.Checker.Services;
using Olymp.Domain;
using Olymp.Site;
using Olymp.Site.Services.Checker;
using Olymp.Site.Services.Contests;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.Site.Contests;

[Collection("Contest management")]
public class RetestTests(SiteFixture site) : IClassFixture<SiteFixture>
{
    [Fact]
    public async Task ContestCompilationRetestOnlyQueuesCompilationErrorsOfActiveProblems()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        var other = await site.AddContestAsync(owner);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        foreach (var number in new[] { 1, 2, 3 })
            await ContestManagementTests.ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.ProblemZip(number));
        await ContestManagementTests.ImportAsync(scope.ServiceProvider, other.Id, ProblemArchives.ProblemZip());
        await db.Problems.Where(x => x.ContestId == contest.Id && x.Number == 3)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), cancellationToken: TestContext.Current.CancellationToken);
        var problems = await db.Problems.Where(x => x.ContestId == contest.Id || x.ContestId == other.Id).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        foreach (var problem in problems)
            await ContestManagementTests.AddSubmissionsAsync(db, owner.Id, problem, [2, -1001, -1, -10, 0]);
        var before = await db.Submissions.Where(x => x.Problem.ContestId == contest.Id || x.Problem.ContestId == other.Id)
            .Include(x => x.CheckResults).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        var path = $"/Contests/Edit/{contest.Id}";
        var stranger = await site.AddUserAsync(RoleNames.Jury);
        using (var strangerClient = await site.LoginAsync(stranger))
        {
            var denied = await SiteFixture.PostAsync(strangerClient, path + "?handler=RetestCompilationErrors", [], "/Contests/Edit");
            Assert.Contains("AccessDenied", denied.Headers.Location!.ToString());
            Assert.Equal(0, await db.Submissions.Where(x => x.Problem.ContestId == contest.Id && x.StatusCode == 0)
                .CountAsync(x => x.CheckResults.Any(r => r.StatusCode == -1), cancellationToken: TestContext.Current.CancellationToken));
        }
        using var client = await site.LoginAsync(owner);
        Assert.Contains("handler=RetestCompilationErrors", await client.GetStringAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Redirect,
            (await SiteFixture.PostAsync(client, path + "?handler=RetestCompilationErrors", [])).StatusCode);
        db.ChangeTracker.Clear();
        var saved = await db.Submissions.Where(x => x.Problem.ContestId == contest.Id || x.Problem.ContestId == other.Id)
            .Include(x => x.CheckResults).ToDictionaryAsync(x => x.Id, cancellationToken: TestContext.Current.CancellationToken);
        var changed = 0;
        foreach (var original in before)
        {
            var result = saved[original.Id];
            var problem = problems.Single(x => x.Id == original.ProblemId);
            var shouldRetest = problem.ContestId == contest.Id && problem.IsActive && original.StatusCode == -1;
            Assert.Equal(shouldRetest ? 0 : original.StatusCode, result.StatusCode);
            Assert.Equal(shouldRetest ? null : original.Score, result.Score);
            Assert.Equal(shouldRetest ? null : original.ScoresByTest, result.ScoresByTest);
            Assert.Equal(original.CheckResults.Single().Id, result.CheckResults.Single().Id);
            if (shouldRetest)
                changed++;
            else
                Assert.Equal(original.LastModification, result.LastModification);
        }
        Assert.Equal(2, changed);
    }

    [Fact]
    public async Task BothPostButtonsUseTheirOwnFilterAndRejectForeignProblem()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        int problemId;
        using (var scope = site.Services.CreateScope())
        {
            await ContestManagementTests.ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.ProblemZip());
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var problem = await db.Problems.SingleAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken);
            problemId = problem.Id;
            await ContestManagementTests.AddSubmissionsAsync(db, owner.Id, problem, [2, -1001]);
        }
        using var client = await site.LoginAsync(owner);
        var path = $"/Contests/Edit/{contest.Id}";
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path + "?handler=RetestExceptAccepted",
            new() { ["problemId"] = problemId.ToString() })).StatusCode);
        int[] expectedStatuses = [2, 0];
        using (var scope = site.Services.CreateScope())
            Assert.Equal(expectedStatuses, await scope.ServiceProvider.GetRequiredService<OlympContext>().Submissions
                .Where(x => x.ProblemId == problemId).OrderBy(x => x.Id).Select(x => x.StatusCode).ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path + "?handler=RetestAll",
            new() { ["problemId"] = problemId.ToString() })).StatusCode);
        using (var scope = site.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<OlympContext>().Submissions
                .Where(x => x.ProblemId == problemId).AllAsync(x => x.StatusCode == 0, cancellationToken: TestContext.Current.CancellationToken));
        var other = await site.AddContestAsync(owner);
        using var otherClient = await site.LoginAsync(owner);
        Assert.Equal(HttpStatusCode.OK, (await SiteFixture.PostAsync(otherClient,
            $"/Contests/Edit/{other.Id}?handler=RetestAll", new() { ["problemId"] = problemId.ToString() })).StatusCode);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 2)]
    public async Task RetestModesPreserveHistoryAndQueuedSubmissions(bool exceptAccepted, int expected)
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await ContestManagementTests.ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.ProblemZip());
        var problem = await db.Problems.SingleAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken);
        await ContestManagementTests.AddSubmissionsAsync(db, owner.Id, problem, [2, -1001, -1, 0]);
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
        await ContestManagementTests.ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.ProblemZip());
        var problem = await db.Problems.SingleAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken);
        await ContestManagementTests.AddSubmissionsAsync(db, owner.Id, problem, [2, 1, -1]);
        var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
        foreach (var exceptAccepted in new[] { false, true })
            Assert.Equal(scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResource>>()[ContestProblemService.BusyMessage].Value,
                (await Assert.ThrowsAsync<InvalidDataException>(() => service.RetestAsync(contest.Id, problem.Id, exceptAccepted, TestContext.Current.CancellationToken))).Message);
        Assert.Equal(scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResource>>()[ContestProblemService.BusyMessage].Value,
            (await Assert.ThrowsAsync<InvalidDataException>(() => service.RetestCompilationErrorsAsync(contest.Id, TestContext.Current.CancellationToken))).Message);
        await Assert.ThrowsAsync<InvalidDataException>(() => ContestManagementTests.ImportAsync(scope.ServiceProvider,
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
            await ContestManagementTests.ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.ProblemZip());
            var problem = await db.Problems.SingleAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken);
            problemId = problem.Id;
            await ContestManagementTests.AddSubmissionsAsync(db, owner.Id, problem, [0, 0]);
            // Other tests deliberately keep queued submissions. This runner should see only this task.
            await db.Problems.Where(x => x.Id != problemId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), cancellationToken: TestContext.Current.CancellationToken);
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
            await Assert.ThrowsAsync<InvalidDataException>(() => ContestManagementTests.ImportAsync(scope.ServiceProvider,
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
            await ContestManagementTests.ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.ProblemZip());
            var problem = await db.Problems.SingleAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken);
            problemId = problem.Id;
            await ContestManagementTests.AddSubmissionsAsync(db, owner.Id, problem, [0]);
            await db.Problems.Where(x => x.Id != problemId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), cancellationToken: TestContext.Current.CancellationToken);
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

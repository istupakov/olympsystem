using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Site.Services.Checker;

using Xunit;

using static Olymp.IntegrationTests.Site.Checkers.CheckerTestData;

namespace Olymp.IntegrationTests.Site.Checkers;

[Trait("Category", "Integration")]
public class QueueTests(SiteFactory factory) : IClassFixture<SiteFactory>
{
    [Fact]
    public async Task ClaimedSubmissionIsNotPickedAgainAndGateIsFreeDuringExecution()
    {
        var submission = Assert.Single(await AddSubmissionsAsync(factory, "acm", [0]));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new TestRunner
        {
            BeforeCompile = async token =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        var checker = await CreateCheckerAsync(runner, submission.Compilator.Name);
        var manager = factory.Services.GetRequiredService<ICheckerManager>();
        var token = TestContext.Current.CancellationToken;
        var check = manager.TryCheckFromDB(checker, token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            using (await manager.EnterAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token))
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var saved = await scope.ServiceProvider.GetRequiredService<OlympContext>().Submissions
                    .SingleAsync(item => item.Id == submission.Id, token);
                Assert.Equal(1, saved.StatusCode);
                Assert.True(saved.LastModification >= submission.LastModification);
            }
            Assert.False(await manager.TryCheckFromDB(checker, token).WaitAsync(TimeSpan.FromSeconds(10), token));
        }
        finally
        {
            release.TrySetResult();
            await check.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        await using var finalScope = factory.Services.CreateAsyncScope();
        var db = finalScope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.Equal(2, (await db.Submissions.SingleAsync(item => item.Id == submission.Id, token)).StatusCode);
        Assert.Equal(2, await db.CheckResults.CountAsync(result => result.SubmissionId == submission.Id, token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledCheckReturnsSubmissionToQueue(bool cancel)
    {
        var submission = Assert.Single(await AddSubmissionsAsync(factory, "acm", [0]));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool fail = !cancel;
        var runner = new TestRunner
        {
            BeforeCompile = async token =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                if (fail)
                    throw new InvalidOperationException("Injected runner failure");
            }
        };
        var checker = await CreateCheckerAsync(runner, submission.Compilator.Name);
        var manager = factory.Services.GetRequiredService<ICheckerManager>();
        var token = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var check = manager.TryCheckFromDB(checker, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            if (cancel)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.WaitAsync(TimeSpan.FromSeconds(10), token));
            }
            else
            {
                release.TrySetResult();
                await Assert.ThrowsAsync<InvalidOperationException>(() => check.WaitAsync(TimeSpan.FromSeconds(10), token));
            }
        }
        finally
        {
            release.TrySetResult();
        }
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var saved = await db.Submissions.SingleAsync(item => item.Id == submission.Id, token);
            Assert.Equal(0, saved.StatusCode);
            Assert.Equal(25, saved.Score);
            Assert.Equal("25;0", saved.ScoresByTest);
            Assert.Equal(1, await db.CheckResults.CountAsync(result => result.SubmissionId == submission.Id, token));
        }
        fail = false;
        Assert.True(await manager.TryCheckFromDB(checker, token).WaitAsync(TimeSpan.FromSeconds(10), token));
        await using var finalScope = factory.Services.CreateAsyncScope();
        var checkedSubmission = await finalScope.ServiceProvider.GetRequiredService<OlympContext>().Submissions
            .Include(item => item.CheckResults).SingleAsync(item => item.Id == submission.Id, token);
        Assert.Equal(2, checkedSubmission.StatusCode);
        Assert.Equal(2, checkedSubmission.CheckResults.Count);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(1, 3, true)]
    [InlineData(2, 0, false)]
    public async Task QueueChecksPendingAndStaleSubmissionsButPreservesRecentOrCompletedChecks(
        int status, int ageMinutes, bool shouldCheck)
    {
        var submission = Assert.Single(await AddSubmissionsAsync(factory, "acm", [status]));
        var token = TestContext.Current.CancellationToken;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Submissions.Where(item => item.Id == submission.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LastModification, DateTimeOffset.UtcNow.AddMinutes(-ageMinutes)), token);
        }
        var runner = new TestRunner();
        var checker = await CreateCheckerAsync(runner, submission.Compilator.Name);

        Assert.Equal(shouldCheck, await factory.Services.GetRequiredService<ICheckerManager>().TryCheckFromDB(checker, token));

        Assert.Equal(shouldCheck ? 4 : 0, runner.TestsRun);
        await using var finalScope = factory.Services.CreateAsyncScope();
        var saved = await finalScope.ServiceProvider.GetRequiredService<OlympContext>().Submissions
            .Include(item => item.CheckResults).SingleAsync(item => item.Id == submission.Id, token);
        Assert.Equal(shouldCheck ? 2 : status, saved.StatusCode);
        Assert.Equal(shouldCheck ? 2 : 1, saved.CheckResults.Count);
    }

    [Theory]
    [InlineData("problem")]
    [InlineData("tests")]
    [InlineData("compiler")]
    [InlineData("version")]
    public async Task QueueSkipsSubmissionsThatCannotBeChecked(string unavailable)
    {
        var submission = Assert.Single(await AddSubmissionsAsync(factory, "acm", [0]));
        var token = TestContext.Current.CancellationToken;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            switch (unavailable)
            {
                case "problem":
                    await db.Problems.Where(problem => problem.Id == submission.ProblemId)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(problem => problem.IsActive, false), token);
                    break;
                case "tests":
                    await db.Tests.Where(test => test.ProblemId == submission.ProblemId)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(test => test.IsActive, false), token);
                    break;
                case "compiler":
                    await db.Compilators.Where(compiler => compiler.Id == submission.CompilatorId)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(compiler => compiler.IsActive, false), token);
                    break;
                case "version":
                    await db.Compilators.Where(compiler => compiler.Id == submission.CompilatorId)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(compiler => compiler.Description, "Different version"), token);
                    break;
            }
        }
        var runner = new TestRunner();
        var checker = await CreateCheckerAsync(runner, submission.Compilator.Name);

        Assert.False(await factory.Services.GetRequiredService<ICheckerManager>().TryCheckFromDB(checker, token));

        Assert.Equal(0, runner.TestsRun);
        await using var finalScope = factory.Services.CreateAsyncScope();
        var saved = await finalScope.ServiceProvider.GetRequiredService<OlympContext>().Submissions
            .SingleAsync(item => item.Id == submission.Id, token);
        Assert.Equal(0, saved.StatusCode);
    }

    [Fact]
    public async Task QueueProcessesOldestSubmissionFirst()
    {
        var submissions = await AddSubmissionsAsync(factory, "acm", [0, 0]);
        var oldest = submissions[1];
        var token = TestContext.Current.CancellationToken;
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OlympContext>().Submissions.Where(item => item.Id == oldest.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CommitTime, oldest.CommitTime.AddMinutes(-5)), token);
        var runner = new TestRunner();
        var checker = await CreateCheckerAsync(runner, oldest.Compilator.Name);
        var manager = factory.Services.GetRequiredService<ICheckerManager>();

        Assert.True(await manager.TryCheckFromDB(checker, token));
        Assert.Equal(oldest.Id + ".c", Assert.Single(runner.Requests.Single(request => request.ClearWorkdir).Files).Filename);
        Assert.True(await manager.TryCheckFromDB(checker, token));
        Assert.False(await manager.TryCheckFromDB(checker, token));
        Assert.Equal(8, runner.TestsRun);
    }
}

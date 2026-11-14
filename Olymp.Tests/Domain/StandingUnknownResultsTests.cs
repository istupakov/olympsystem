using Olymp.Domain;

using Xunit;

namespace Olymp.Tests.Domain;

[Trait("Category", "OlympSystem")]
public class StandingUnknownResultsTests
{
    [Theory]
    [InlineData(0, 2, 1, false)]
    [InlineData(1, 2, 1, false)]
    [InlineData(-1, 2, 0, false)]
    [InlineData(-1001, 3, 0, false)]
    [InlineData(2, 3, 0, true)]
    public void PendingAttemptCanBecomeCompilationErrorFailureOrSuccess(int status, int attempts, int unknown, bool solved)
    {
        var scenario = new ContestScenario();
        var user = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(user, problem, 5, -1001);
        scenario.Submit(user, problem, 10, -1001);
        scenario.Submit(user, problem, 15, status);

        var result = Assert.Single(scenario.GetResults());
        var cell = Assert.Single(result.ProblemResults);
        Assert.Equal(attempts, cell.CommitNumber);
        Assert.Equal(unknown, cell.UnknownCommitNumber);
        Assert.Equal(unknown > 0 ? scenario.Contest.StartTime.AddMinutes(15) : (DateTimeOffset?)null, cell.LastUnknownCommitTime);
        Assert.Equal(solved, cell.IsSuccess);
        Assert.Equal(solved ? 1 : 0, result.Score);
        Assert.Equal(solved ? TimeSpan.FromMinutes(55) : TimeSpan.Zero, result.SummaryTime);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(-1001)]
    [InlineData(2)]
    public void FrozenAttemptsDoNotRevealTheirVerdictOrScore(int status)
    {
        var scenario = new ContestScenario();
        scenario.Contest.FreezeTime = scenario.Contest.StartTime.AddHours(1);
        var user = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(user, problem, 10, -1001);
        scenario.Submit(user, problem, 20, -1001);
        scenario.Submit(user, problem, 60, status, 100);

        var result = Assert.Single(scenario.GetResults());
        var cell = Assert.Single(result.ProblemResults);
        Assert.Equal(2, cell.CommitNumber);
        Assert.Equal(1, cell.UnknownCommitNumber);
        Assert.Equal(scenario.Contest.StartTime.AddMinutes(20), cell.LastCommitTime);
        Assert.Equal(scenario.Contest.StartTime.AddMinutes(60), cell.LastUnknownCommitTime);
        Assert.False(cell.IsSuccess);
        Assert.False(cell.IsFirstSuccess);
        Assert.Equal(0, cell.Score);
        Assert.Equal(0, result.Score);
        Assert.Equal(TimeSpan.Zero, result.SummaryTime);
    }

    [Fact]
    public void PendingAndFrozenAttemptsAreCountedOnceAndHiddenSubmissionsAreExcluded()
    {
        var scenario = new ContestScenario();
        scenario.Contest.FreezeTime = scenario.Contest.StartTime.AddHours(1);
        var user = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(user, problem, 10, -1);
        scenario.Submit(user, problem, 20, 0);
        scenario.Submit(user, problem, 60, 1);
        scenario.Submit(user, problem, 61, -1);
        scenario.Submit(user, problem, 62, 2).IsHidden = true;

        var cell = Assert.Single(Assert.Single(scenario.GetResults()).ProblemResults);
        Assert.Equal(0, cell.CommitNumber);
        Assert.Equal(3, cell.UnknownCommitNumber);
        Assert.Equal(default, cell.LastCommitTime);
        Assert.Equal(scenario.Contest.StartTime.AddMinutes(61), cell.LastUnknownCommitTime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KnownSuccessRetainsItsResultDespiteUnknownAttempts(bool freeze)
    {
        var scenario = new ContestScenario();
        if (freeze) scenario.Contest.FreezeTime = scenario.Contest.StartTime.AddHours(1);
        var user = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(user, problem, 5, 0);
        scenario.Submit(user, problem, 10, -1001);
        scenario.Submit(user, problem, 20, 2);
        scenario.Submit(user, problem, 60, 1);
        scenario.Submit(user, problem, 61, 2);

        var result = Assert.Single(scenario.GetResults());
        var cell = Assert.Single(result.ProblemResults);
        Assert.True(cell.IsSuccess);
        Assert.True(cell.IsFirstSuccess);
        Assert.Equal(2, cell.CommitNumber);
        Assert.Equal(0, cell.UnknownCommitNumber);
        Assert.Equal(TimeSpan.FromMinutes(40), result.SummaryTime);
    }

    [Fact]
    public void UnfreezingRevealsResultsAndRemovesCompilationErrorsFromAttempts()
    {
        var scenario = new ContestScenario();
        scenario.Contest.FreezeTime = scenario.Contest.StartTime.AddHours(1);
        var user = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(user, problem, 10, -1001);
        scenario.Submit(user, problem, 60, -1);
        scenario.Submit(user, problem, 70, 2);

        var frozen = Assert.Single(Assert.Single(scenario.GetResults()).ProblemResults);
        Assert.Equal(2, frozen.UnknownCommitNumber);
        Assert.False(frozen.IsSuccess);

        var result = Assert.Single(scenario.GetResults(notFreeze: true));
        var cell = Assert.Single(result.ProblemResults);
        Assert.Equal(0, cell.UnknownCommitNumber);
        Assert.Equal(2, cell.CommitNumber);
        Assert.True(cell.IsSuccess);
        Assert.True(cell.IsFirstSuccess);
        Assert.Equal(TimeSpan.FromMinutes(90), result.SummaryTime);
    }

    [Fact]
    public void DirectProblemProgressCountsPendingAttemptsWithoutTreatingStaleScoreAsSuccess()
    {
        var scenario = new ContestScenario();
        var user = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(user, problem, 10, 1, 100);

        var cell = Assert.Single(Standing.UserProblemStanding([problem], problem.Submissions));
        Assert.Equal(1, cell.UnknownCommitNumber);
        Assert.Equal(0, cell.CommitNumber);
        Assert.Equal(0, cell.Score);
        Assert.False(cell.IsSuccess);
    }

    [Fact]
    public void FreezeUsesTheSuppliedCalculationTimeAndHasAStrictBoundary()
    {
        var scenario = new ContestScenario();
        var freeze = scenario.Contest.StartTime.AddHours(1);
        scenario.Contest.FreezeTime = freeze;
        var user = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(user, problem, 60, 2);

        var atBoundary = Standing.Create(scenario.Contest, false, true, false, null, freeze);
        Assert.False(atBoundary.IsFrozen);
        Assert.True(Assert.Single(Assert.Single(atBoundary.Results).ProblemResults).IsSuccess);

        var afterBoundary = Standing.Create(scenario.Contest, false, true, false, null, freeze.AddTicks(1));
        Assert.True(afterBoundary.IsFrozen);
        var cell = Assert.Single(Assert.Single(afterBoundary.Results).ProblemResults);
        Assert.False(cell.IsSuccess);
        Assert.Equal(1, cell.UnknownCommitNumber);
    }

    [Fact]
    public void ResultsAreMaterializedAndDoNotChangeAfterRejudgingTheSourceSubmission()
    {
        var scenario = new ContestScenario();
        var user = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        var submission = scenario.Submit(user, problem, 10, 0);
        var standing = Standing.Create(scenario.Contest, false, true, false, null);
        submission.StatusCode = 2;

        Assert.Equal(0, Assert.Single(standing.Results).Score);
        Assert.Equal(1, Assert.Single(Assert.Single(standing.Results).ProblemResults).UnknownCommitNumber);
        Assert.Equal(1, Assert.Single(Standing.Create(scenario.Contest, false, true, false, null).Results).Score);
    }
}

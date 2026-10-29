using Olymp.Domain;

using Xunit;

#pragma warning disable xUnit1004 // Known ICPC deviations are retained as skipped regression tests.

namespace Olymp.Tests.Domain;

// Scoring and verdicts: https://ccs-specs.icpc.io/2026-01/ccs_system_requirements
[Trait("Category", "ICPC")]
public class IcpcStandingTests
{
    private const int Accepted = 2;
    private const int WrongAnswer = -1001;

    [Fact]
    public void FirstAcceptedSubmissionSolvesOneProblem()
    {
        var scenario = new ContestScenario();
        var competitor = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(competitor, problem, 30, Accepted);

        var result = Assert.Single(scenario.GetResults());

        Assert.Equal(1, result.Score);
        Assert.Equal(TimeSpan.FromMinutes(30), result.SummaryTime);
    }

    [Theory]
    [InlineData(-1001)] // Wrong answer
    [InlineData(-2001)] // Time limit
    [InlineData(-3001)] // Runtime error
    [InlineData(-4001)] // Presentation error
    [InlineData(-5001)] // Idleness limit
    [InlineData(-6001)] // Memory limit
    public void EachRejectedAttemptBeforeAcceptanceAddsTwentyMinutes(int status)
    {
        var scenario = new ContestScenario();
        var competitor = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(competitor, problem, 10, status);
        scenario.Submit(competitor, problem, 20, status);
        scenario.Submit(competitor, problem, 30, Accepted);

        var result = Assert.Single(scenario.GetResults());

        Assert.Equal(1, result.Score);
        Assert.Equal(TimeSpan.FromMinutes(70), result.SummaryTime);
    }

    [Theory]
    [InlineData(-1)] // Compilation error
    [InlineData(-10, Skip = "Standing currently adds penalty for internal judging errors.")]
    public void CompilationAndJudgingErrorsDoNotAddPenalty(int status)
    {
        var scenario = new ContestScenario();
        var competitor = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(competitor, problem, 10, status);
        scenario.Submit(competitor, problem, 20, Accepted);

        var result = Assert.Single(scenario.GetResults());

        Assert.Equal(1, result.Score);
        Assert.Equal(TimeSpan.FromMinutes(20), result.SummaryTime);
    }

    [Fact]
    public void UnsolvedProblemsContributeNeitherScoreNorPenalty()
    {
        var scenario = new ContestScenario();
        var competitor = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(competitor, problem, 10, WrongAnswer);
        scenario.Submit(competitor, problem, 20, WrongAnswer);

        var result = Assert.Single(scenario.GetResults());

        Assert.Equal(0, result.Score);
        Assert.Equal(TimeSpan.Zero, result.SummaryTime);
    }

    [Fact]
    public void FailedAttemptsOnUnsolvedProblemDoNotIncreaseTotalPenalty()
    {
        var scenario = new ContestScenario();
        var competitor = scenario.AddCompetitor(1);
        var solved = scenario.AddProblem();
        var unsolved = scenario.AddProblem();
        scenario.Submit(competitor, unsolved, 10, WrongAnswer);
        scenario.Submit(competitor, unsolved, 20, WrongAnswer);
        scenario.Submit(competitor, solved, 30, Accepted);

        var result = Assert.Single(scenario.GetResults());

        Assert.Equal(1, result.Score);
        Assert.Equal(TimeSpan.FromMinutes(30), result.SummaryTime);
    }

    [Fact]
    public void TotalPenaltyIsSumOfSolvedProblemTimesAndTheirOwnFailedAttempts()
    {
        var scenario = new ContestScenario();
        var competitor = scenario.AddCompetitor(1);
        var first = scenario.AddProblem();
        var second = scenario.AddProblem();
        scenario.Submit(competitor, first, 5, WrongAnswer);
        scenario.Submit(competitor, first, 10, Accepted);
        scenario.Submit(competitor, second, 15, WrongAnswer);
        scenario.Submit(competitor, second, 20, WrongAnswer);
        scenario.Submit(competitor, second, 30, Accepted);

        var result = Assert.Single(scenario.GetResults());

        Assert.Equal(2, result.Score);
        Assert.Equal(TimeSpan.FromMinutes(100), result.SummaryTime);
    }

    [Fact]
    public void AttemptsAfterFirstAcceptedSubmissionDoNotChangeResult()
    {
        var scenario = new ContestScenario();
        var competitor = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(competitor, problem, 10, WrongAnswer);
        scenario.Submit(competitor, problem, 20, Accepted);
        scenario.Submit(competitor, problem, 30, WrongAnswer);
        scenario.Submit(competitor, problem, 40, Accepted);

        var result = Assert.Single(scenario.GetResults());

        Assert.Equal(1, result.Score);
        Assert.Equal(TimeSpan.FromMinutes(40), result.SummaryTime);
        Assert.Equal(scenario.Contest.StartTime.AddMinutes(20), Assert.Single(result.ProblemResults).LastCommitTime);
    }

    [Fact(Skip = "Standing currently counts attempts after acceptance when their timestamps match.")]
    public void AttemptsAfterAcceptanceAtSameTimestampDoNotAddPenalty()
    {
        var scenario = new ContestScenario();
        var competitor = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(competitor, problem, 20, Accepted);
        scenario.Submit(competitor, problem, 20, WrongAnswer);

        var result = Assert.Single(scenario.GetResults());

        Assert.Equal(1, result.Score);
        Assert.Equal(TimeSpan.FromMinutes(20), result.SummaryTime);
    }

    [Fact]
    public void FirstAcceptanceIsSelectedBySubmissionTimeRatherThanCollectionOrder()
    {
        var scenario = new ContestScenario();
        var competitor = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        var first = scenario.Submit(competitor, problem, 10, Accepted);
        var second = scenario.Submit(competitor, problem, 20, Accepted);
        problem.Submissions.Clear();
        problem.Submissions.Add(second);
        problem.Submissions.Add(first);

        var result = Assert.Single(scenario.GetResults());

        Assert.Equal(1, result.Score);
        Assert.Equal(TimeSpan.FromMinutes(10), result.SummaryTime);
    }

    [Fact]
    public void JudgingAfterContestEndsDoesNotInvalidateTimelySubmission()
    {
        var scenario = new ContestScenario();
        var competitor = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        var submission = scenario.Submit(competitor, problem, 299, Accepted);
        submission.LastModification = scenario.Contest.EndTime.AddMinutes(10);

        var result = Assert.Single(scenario.GetResults());

        Assert.Equal(1, result.Score);
        Assert.Equal(TimeSpan.FromMinutes(299), result.SummaryTime);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(59999, 0, Skip = "Standing currently preserves fractional minutes.")]
    [InlineData(60000, 1)]
    [InlineData(119999, 1, Skip = "Standing currently preserves fractional minutes.")]
    [InlineData(120000, 2)]
    public void AcceptedSubmissionTimeIsRoundedDownToWholeMinutes(int milliseconds, int expectedMinutes)
    {
        var scenario = new ContestScenario();
        var competitor = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(competitor, problem, TimeSpan.FromMilliseconds(milliseconds), Accepted);

        var result = Assert.Single(scenario.GetResults());

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), result.SummaryTime);
    }

    [Fact(Skip = "Standing currently sums exact submission times without rounding each problem to minutes.")]
    public void EachSolvedProblemTimeIsRoundedBeforeSumming()
    {
        var scenario = new ContestScenario();
        var competitor = scenario.AddCompetitor(1);
        var first = scenario.AddProblem();
        var second = scenario.AddProblem();
        scenario.Submit(competitor, first, TimeSpan.FromMilliseconds(119999), Accepted);
        scenario.Submit(competitor, second, TimeSpan.FromMilliseconds(179999), Accepted);

        var result = Assert.Single(scenario.GetResults());

        Assert.Equal(2, result.Score);
        Assert.Equal(TimeSpan.FromMinutes(3), result.SummaryTime);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(17999999, 1)]
    [InlineData(18000001, 0)]
    public void OnlySubmissionsWithinContestWindowCount(int milliseconds, int expectedScore)
    {
        var scenario = new ContestScenario();
        var competitor = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(competitor, problem, TimeSpan.FromMilliseconds(milliseconds), Accepted);

        var result = Assert.Single(scenario.GetResults());

        Assert.Equal(expectedScore, result.Score);
    }

    [Fact]
    public void MoreSolvedProblemsRankHigherRegardlessOfTotalPenalty()
    {
        var scenario = new ContestScenario();
        var moreSolved = scenario.AddCompetitor(1);
        var lessSolved = scenario.AddCompetitor(2);
        var first = scenario.AddProblem();
        var second = scenario.AddProblem();
        scenario.Submit(moreSolved, first, 100, Accepted);
        scenario.Submit(moreSolved, second, 200, Accepted);
        scenario.Submit(lessSolved, first, 1, Accepted);

        var results = scenario.GetResults();

        Assert.Equal([1, 2], results.Select(result => result.User.Id));
    }

    [Fact]
    public void LowerTotalPenaltyRanksHigherWhenSolvedCountsAreEqual()
    {
        var scenario = new ContestScenario();
        var higherPenalty = scenario.AddCompetitor(1);
        var lowerPenalty = scenario.AddCompetitor(2);
        var problem = scenario.AddProblem();
        scenario.Submit(higherPenalty, problem, 5, WrongAnswer);
        scenario.Submit(higherPenalty, problem, 10, Accepted);
        scenario.Submit(lowerPenalty, problem, 20, Accepted);

        var results = scenario.GetResults();

        Assert.Equal([2, 1], results.Select(result => result.User.Id));
    }

    [Fact(Skip = "Standing currently sorts equal scores and penalties by name instead of last acceptance time.")]
    public void EarlierLastAcceptanceBreaksEqualScoreAndPenaltyBeforeName()
    {
        var scenario = new ContestScenario();
        var alice = scenario.AddCompetitor(1);
        alice.Name = "Alice";
        var bob = scenario.AddCompetitor(2);
        bob.Name = "Bob";
        var first = scenario.AddProblem();
        var second = scenario.AddProblem();
        scenario.Submit(alice, first, 10, Accepted);
        scenario.Submit(alice, second, 50, Accepted);
        scenario.Submit(bob, first, 20, Accepted);
        scenario.Submit(bob, second, 40, Accepted);

        var results = scenario.GetResults();

        Assert.Equal([2, 1], results.Select(result => result.User.Id));
    }
}

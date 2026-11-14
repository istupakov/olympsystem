using Xunit;

namespace Olymp.Tests.Domain;

[Trait("Category", "OlympSystem")]
public class StandingTests
{
    private const int Accepted = 2;
    private const int WrongAnswer = -1001;

    [Theory]
    [InlineData(2025, false, null, 119)]
    [InlineData(2026, false, null, 60)]
    [InlineData(2027, false, null, 60)]
    [InlineData(2025, false, true, 60)]
    [InlineData(2026, false, false, 119)]
    [InlineData(2026, true, null, 119)]
    [InlineData(2026, true, true, 119)]
    public void TimeRoundingDefaultsToContestsFrom2026AndCanBeOverridden(int year, bool school, bool? roundTime, int seconds)
    {
        var scenario = new ContestScenario();
        scenario.Contest.StartTime = new DateTimeOffset(year, 1, 1, 10, 0, 0, TimeSpan.Zero);
        scenario.Contest.EndTime = scenario.Contest.StartTime.AddHours(5);
        if (school) scenario.Contest.Abbr = "school-test";
        var competitor = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(competitor, problem, TimeSpan.FromSeconds(119), Accepted);

        var standing = Olymp.Domain.Standing.Create(scenario.Contest, false, true, true, null, roundTime: roundTime);

        Assert.Equal(TimeSpan.FromSeconds(seconds), Assert.Single(standing.Results).SummaryTime);
    }

    [Fact]
    public void TrainingUsesOriginalContestYearForRounding()
    {
        var scenario = new ContestScenario();
        var user = ContestScenario.CreateOlympUser(1);
        var problem = scenario.AddProblem();
        var start = scenario.Contest.StartTime.AddYears(6);
        scenario.Submit(user, problem, 0, Accepted).CommitTime = start.AddSeconds(119);

        var standing = Olymp.Domain.Standing.Create(scenario.Contest, true, false, true, start, start.AddMinutes(3));

        Assert.Equal(TimeSpan.FromSeconds(119), Assert.Single(standing.Results).SummaryTime);
    }

    [Fact]
    public void SchoolContestPreservesExactTimesAndNameTieBreaker()
    {
        var scenario = new ContestScenario();
        scenario.Contest.Abbr = "school-test";
        var alice = scenario.AddCompetitor(1);
        alice.Name = "Alice";
        var bob = scenario.AddCompetitor(2);
        bob.Name = "Bob";
        var first = scenario.AddProblem();
        var second = scenario.AddProblem();
        scenario.Submit(alice, first, TimeSpan.FromSeconds(61), Accepted, score: 100);
        scenario.Submit(alice, second, TimeSpan.FromSeconds(179), Accepted, score: 100);
        scenario.Submit(bob, first, TimeSpan.FromSeconds(119), Accepted, score: 100);
        scenario.Submit(bob, second, TimeSpan.FromSeconds(121), Accepted, score: 100);

        var results = scenario.GetResults();

        Assert.Equal([1, 2], results.Select(result => result.User.Id));
        Assert.All(results, result => Assert.Equal(TimeSpan.FromSeconds(240), result.SummaryTime));
        Assert.Equal(scenario.Contest.StartTime.AddSeconds(179), results[0].ProblemResults.Last().LastCommitTime);
    }

    [Fact]
    public void ApprovedCompetitorWithoutSubmissionsAppearsWithZeroScore()
    {
        var scenario = new ContestScenario();
        var competitor = scenario.AddCompetitor(1);
        scenario.AddProblem();

        var result = Assert.Single(scenario.GetResults());

        Assert.Same(competitor, result.User);
        Assert.Equal(0, result.Score);
        Assert.Equal(TimeSpan.Zero, result.SummaryTime);
        var problemResult = Assert.Single(result.ProblemResults);
        Assert.False(problemResult.IsSuccess);
        Assert.Equal(0, problemResult.CommitNumber);
    }

    [Fact]
    public void SchoolContestDoesNotAddPenaltyForFailedAttempts()
    {
        var scenario = new ContestScenario();
        scenario.Contest.Abbr = "school-test";
        var competitor = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(competitor, problem, 10, WrongAnswer);
        scenario.Submit(competitor, problem, 20, WrongAnswer);
        scenario.Submit(competitor, problem, 30, Accepted);

        var result = Assert.Single(scenario.GetResults());

        Assert.Equal(1, result.Score);
        Assert.Equal(TimeSpan.FromMinutes(30), result.SummaryTime);
        Assert.Equal(3, Assert.Single(result.ProblemResults).CommitNumber);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PendingSubmissionsDoNotAffectCurrentStanding(int status)
    {
        var scenario = new ContestScenario();
        var competitor = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(competitor, problem, 10, status);
        scenario.Submit(competitor, problem, 20, Accepted);

        var result = Assert.Single(scenario.GetResults());

        Assert.Equal(TimeSpan.FromMinutes(20), result.SummaryTime);
        Assert.Equal(1, Assert.Single(result.ProblemResults).CommitNumber);
    }

    [Fact]
    public void SchoolContestUsesHighestPartialScoreAndEarliestAttemptWithThatScore()
    {
        var scenario = new ContestScenario();
        scenario.Contest.Abbr = "school-test";
        var competitor = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(competitor, problem, 10, WrongAnswer, score: 20);
        scenario.Submit(competitor, problem, 30, WrongAnswer, score: 60);
        scenario.Submit(competitor, problem, 20, WrongAnswer, score: 60);
        scenario.Submit(competitor, problem, 40, WrongAnswer, score: 40);

        var result = Assert.Single(scenario.GetResults());

        Assert.Equal(60, result.Score);
        Assert.Equal(TimeSpan.FromMinutes(20), result.SummaryTime);
        Assert.Equal(2, Assert.Single(result.ProblemResults).CommitNumber);
    }

    [Theory]
    [InlineData(59, false, 1)]
    [InlineData(60, false, 0)]
    [InlineData(61, false, 0)]
    [InlineData(60, true, 1)]
    [InlineData(61, true, 1)]
    public void FrozenStandingExcludesSubmissionsAtOrAfterFreezeUntilUnfrozen(
        int minutes, bool notFreeze, int expectedScore)
    {
        var scenario = new ContestScenario();
        scenario.Contest.FreezeTime = scenario.Contest.StartTime.AddHours(1);
        var competitor = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(competitor, problem, minutes, Accepted);

        var result = Assert.Single(scenario.GetResults(notFreeze: notFreeze));

        Assert.Equal(expectedScore, result.Score);
    }

    [Fact]
    public void HiddenSubmissionsDoNotAffectScoreOrPenalty()
    {
        var scenario = new ContestScenario();
        var competitor = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(competitor, problem, 10, WrongAnswer).IsHidden = true;
        scenario.Submit(competitor, problem, 20, Accepted).IsHidden = true;

        var result = Assert.Single(scenario.GetResults());

        Assert.Equal(0, result.Score);
        Assert.Equal(TimeSpan.Zero, result.SummaryTime);
        Assert.Equal(0, Assert.Single(result.ProblemResults).CommitNumber);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OfficialStandingExcludesOutOfCompetitionAndDisqualifiedParticipants(bool official)
    {
        var scenario = new ContestScenario();
        scenario.AddCompetitor(1);
        scenario.AddCompetitor(2).IsApproved = false;
        scenario.AddCompetitor(3).IsHidden = true;
        scenario.AddCompetitor(4).IsOutOfCompetition = true;
        scenario.AddCompetitor(5).IsDisqualified = true;

        var results = scenario.GetResults(official: official);

        int[] expectedIds = official ? [1] : [1, 4, 5];
        Assert.Equal(expectedIds, results.Select(result => result.User.Id));
    }

    [Fact]
    public void FirstSuccessBelongsToEarliestAcceptedSubmissionAcrossParticipants()
    {
        var scenario = new ContestScenario();
        var first = scenario.AddCompetitor(1);
        var second = scenario.AddCompetitor(2);
        var problem = scenario.AddProblem();
        scenario.Submit(first, problem, 20, Accepted);
        scenario.Submit(second, problem, 10, Accepted);

        var results = scenario.GetResults();

        Assert.Collection(results,
            result =>
            {
                Assert.Same(second, result.User);
                Assert.True(Assert.Single(result.ProblemResults).IsFirstSuccess);
            },
            result =>
            {
                Assert.Same(first, result.User);
                Assert.False(Assert.Single(result.ProblemResults).IsFirstSuccess);
            });
    }

    [Fact]
    public void FullyTiedResultsAreDisplayedInNameOrder()
    {
        var scenario = new ContestScenario();
        var diana = scenario.AddCompetitor(4);
        diana.Name = "Diana";
        var bob = scenario.AddCompetitor(2);
        bob.Name = "Bob";
        var problem = scenario.AddProblem();
        scenario.Submit(diana, problem, 20, Accepted);
        scenario.Submit(bob, problem, 20, Accepted);

        var results = scenario.GetResults();

        Assert.Equal([2, 4], results.Select(result => result.User.Id));
    }
}

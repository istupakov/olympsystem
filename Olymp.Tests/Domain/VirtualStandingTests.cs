using Olymp.Domain;

using Xunit;

namespace Olymp.Tests.Domain;

[Trait("Category", "OlympSystem")]
public class VirtualStandingTests
{
    [Fact]
    public void LastAcceptanceTieBreakerAndMinuteRoundingUseEachParticipantsStart()
    {
        var scenario = new ContestScenario();
        var original = scenario.AddCompetitor(1);
        original.Name = "Alice";
        var newcomer = ContestScenario.CreateOlympUser(2);
        newcomer.Name = "Bob";
        var first = scenario.AddProblem();
        var second = scenario.AddProblem();
        var start = scenario.Contest.StartTime.AddYears(6).AddSeconds(15);
        scenario.Submit(original, first, TimeSpan.FromSeconds(659), 2);
        scenario.Submit(original, second, TimeSpan.FromSeconds(3059), 2);
        scenario.Submit(newcomer, first, 0, 2).CommitTime = start.AddSeconds(1201);
        scenario.Submit(newcomer, second, 0, 2).CommitTime = start.AddSeconds(2401);

        var rows = Standing.Create(scenario.Contest, true, false, false, start, start.AddHours(1), roundTime: true).Results.ToArray();

        Assert.Equal([newcomer.Id, original.Id], rows.Select(row => row.User.Id));
        Assert.All(rows, row => Assert.Equal(TimeSpan.FromMinutes(60), row.SummaryTime));
        Assert.Equal(start.AddSeconds(2401), rows[0].ProblemResults.Last().LastCommitTime);
    }

    [Fact]
    public void DistantFutureStartDoesNotUnderflowOriginalParticipantTime()
    {
        var scenario = new ContestScenario();
        var original = scenario.AddCompetitor(1);
        var problem = scenario.AddProblem();
        scenario.Submit(original, problem, 0, 2);

        var standing = Standing.Create(scenario.Contest, true, false, false,
            DateTimeOffset.MaxValue.AddDays(-1), scenario.Contest.StartTime.AddYears(6));

        Assert.Equal(0, Assert.Single(standing.Results).Score);
        Assert.Equal(0, Assert.Single(Assert.Single(standing.Results).ProblemResults).CommitNumber);
    }

    [Fact]
    public void OriginalAndVirtualParticipantsCompeteAtTheSameElapsedTimeWithoutFreeze()
    {
        var scenario = new ContestScenario();
        var original = scenario.AddCompetitor(1);
        var idle = scenario.AddCompetitor(2);
        var newcomer = ContestScenario.CreateOlympUser(3);
        var problem = scenario.AddProblem();
        var nextProblem = scenario.AddProblem();
        scenario.Contest.FreezeTime = scenario.Contest.StartTime.AddMinutes(5);
        var start = scenario.Contest.StartTime.AddYears(6);
        scenario.Submit(original, problem, 10, -1001);
        scenario.Submit(original, problem, 30, 2);
        scenario.Submit(original, nextProblem, 50, 2);
        scenario.Submit(newcomer, problem, 0, -1001).CommitTime = start.AddMinutes(5);
        scenario.Submit(newcomer, problem, 0, 2).CommitTime = start.AddMinutes(20);
        scenario.Submit(newcomer, nextProblem, 0, 2).CommitTime = start.AddMinutes(50);

        var standing = Standing.Create(scenario.Contest, true, false, false, start, start.AddMinutes(40));
        Assert.Equal(start, standing.VirtualStartTime);
        Assert.False(standing.IsFrozen);
        Assert.False(standing.IsOfficial);
        var rows = standing.Results.ToArray();
        Assert.Equal([newcomer.Id, original.Id, idle.Id], rows.Select(row => row.User.Id));
        Assert.Equal([1, 1, 0], rows.Select(row => row.Score));
        Assert.Equal(TimeSpan.FromMinutes(40), rows[0].SummaryTime);
        Assert.Equal(TimeSpan.FromMinutes(50), rows[1].SummaryTime);
        Assert.True(rows[0].IsVirtual);
        Assert.False(rows[1].IsVirtual);
        Assert.Equal(start, rows[0].StartTime);
        Assert.Equal(scenario.Contest.StartTime, rows[1].StartTime);
        Assert.True(rows[0].ProblemResults.First().IsFirstSuccess);
        Assert.False(rows[1].ProblemResults.First().IsFirstSuccess);
        Assert.All(rows.SelectMany(row => row.ProblemResults), cell => Assert.Equal(0, cell.UnknownCommitNumber));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(40, 2)]
    [InlineData(300, 4)]
    [InlineData(600, 4)]
    public void ReplayIncludesBoundaryAttemptsAndStopsAtTheOriginalDuration(int elapsedMinutes, int attempts)
    {
        var scenario = new ContestScenario();
        var original = scenario.AddCompetitor(1);
        var newcomer = ContestScenario.CreateOlympUser(2);
        var problem = scenario.AddProblem();
        var start = scenario.Contest.StartTime.AddYears(6);
        foreach (int minute in new[] { -1, 0, 40, 41, 300, 301 })
        {
            scenario.Submit(original, problem, minute, -1001);
            scenario.Submit(newcomer, problem, 0, -1001).CommitTime = start.AddMinutes(minute);
        }

        var rows = Standing.Create(scenario.Contest, true, false, false, start, start.AddMinutes(elapsedMinutes)).Results.ToArray();
        Assert.Equal(attempts > 0 ? 2 : 1, rows.Length);
        Assert.All(rows, row => Assert.Equal(attempts, Assert.Single(row.ProblemResults).CommitNumber));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void VirtualModeIncludesTheOriginalRosterAndPersonalAccountsWithoutOfficialFiltering(bool includeAll, bool official)
    {
        var scenario = new ContestScenario();
        scenario.AddCompetitor(1);
        scenario.AddCompetitor(2).IsOutOfCompetition = true;
        scenario.AddCompetitor(3).IsDisqualified = true;
        var hidden = scenario.AddCompetitor(4);
        hidden.IsHidden = true;
        var unapproved = scenario.AddCompetitor(5);
        unapproved.IsApproved = false;
        var foreign = scenario.AddCompetitor(6);
        scenario.Contest.Competitors.Remove(foreign);
        foreign.ContestId = 99;
        var personal = ContestScenario.CreateOlympUser(7);
        var hiddenPersonal = ContestScenario.CreateOlympUser(8);
        hiddenPersonal.IsHidden = true;
        var hiddenSubmissionAuthor = ContestScenario.CreateOlympUser(9);
        var problem = scenario.AddProblem();
        var start = scenario.Contest.StartTime.AddYears(6);
        foreach (var user in new Olymp.Domain.Models.User[] { hidden, unapproved, foreign, personal, hiddenPersonal, hiddenSubmissionAuthor })
        {
            var submission = scenario.Submit(user, problem, 10, 0);
            if (user is Olymp.Domain.Models.OlympUser) submission.CommitTime = start.AddMinutes(10);
            if (user == hiddenSubmissionAuthor) submission.IsHidden = true;
        }

        var standing = Standing.Create(scenario.Contest, includeAll, official, false, start, start.AddMinutes(20));
        Assert.False(standing.IsOfficial);
        Assert.Equal([1, 2, 3, 7], standing.Results.Select(row => row.User.Id));
        var cell = Assert.Single(standing.Results.Single(row => row.User.Id == personal.Id).ProblemResults);
        Assert.Equal(1, cell.UnknownCommitNumber);
    }

    [Fact]
    public void OriginalPracticeAttemptsDoNotLeakIntoReplayAndPersonalHistoryDoesNotLeakIntoVirtualRun()
    {
        var scenario = new ContestScenario();
        var original = scenario.AddCompetitor(1);
        var newcomer = ContestScenario.CreateOlympUser(2);
        var problem = scenario.AddProblem();
        var start = scenario.Contest.StartTime.AddYears(6);
        scenario.Submit(original, problem, 0, 2).CommitTime = start.AddMinutes(10);
        scenario.Submit(newcomer, problem, 10, 2);
        scenario.Submit(newcomer, problem, 0, 0).CommitTime = start.AddMinutes(20);

        var rows = Standing.Create(scenario.Contest, true, false, false, start, start.AddMinutes(30)).Results.ToArray();
        Assert.All(rows, row => Assert.Equal(0, row.Score));
        Assert.Equal(0, Assert.Single(rows[0].ProblemResults).UnknownCommitNumber);
        Assert.Equal(1, Assert.Single(rows[1].ProblemResults).UnknownCommitNumber);
    }

    [Fact]
    public void EqualRelativeFirstAcceptTimesUseStableOriginalParticipantOrder()
    {
        var scenario = new ContestScenario();
        var original = scenario.AddCompetitor(1);
        var newcomer = ContestScenario.CreateOlympUser(2);
        var problem = scenario.AddProblem();
        var start = scenario.Contest.StartTime.AddYears(6);
        scenario.Submit(newcomer, problem, 0, 2).CommitTime = start.AddMinutes(10);
        scenario.Submit(original, problem, 10, 2);

        var rows = Standing.Create(scenario.Contest, true, false, false, start, start.AddMinutes(20)).Results.ToArray();
        Assert.True(Assert.Single(rows[0].ProblemResults).IsFirstSuccess);
        Assert.False(Assert.Single(rows[1].ProblemResults).IsFirstSuccess);
        Assert.Equal(rows[0].SummaryTime, rows[1].SummaryTime);
    }
}

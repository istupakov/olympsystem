using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;

namespace Olymp.Tests.Domain;

internal sealed class ContestScenario
{
    private readonly Compilator _compilator = TestEntities.CreateCompilator();

    public ContestScenario()
    {
        _compilator.Id = 1;
        Contest.Id = 1;
    }

    public Contest Contest { get; } = TestEntities.CreateContest("Test contest",
        new DateTimeOffset(2020, 1, 1, 10, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2020, 1, 1, 15, 0, 0, TimeSpan.Zero));

    public Competitor AddCompetitor(int id)
    {
        var competitor = TestEntities.CreateCompetitor(Contest, $"User {id}");
        competitor.Id = id;
        Contest.Competitors.Add(competitor);
        return competitor;
    }

    public Problem AddProblem()
    {
        int number = Contest.Problems.Count + 1;
        var problem = TestEntities.CreateProblem(Contest, number);
        problem.Id = number;
        Contest.Problems.Add(problem);
        return problem;
    }

    public Submission Submit(Competitor competitor, Problem problem, int minutes, int status, int? score = null)
    {
        return Submit(competitor, problem, TimeSpan.FromMinutes(minutes), status, score);
    }

    public Submission Submit(Competitor competitor, Problem problem, TimeSpan elapsed, int status, int? score = null)
    {
        var submission = TestEntities.CreateSubmission(competitor, problem, _compilator, Contest.StartTime + elapsed);
        submission.Id = _compilator.Submissions.Count + 1;
        submission.StatusCode = status;
        submission.Score = score;
        competitor.Submissions.Add(submission);
        problem.Submissions.Add(submission);
        _compilator.Submissions.Add(submission);
        return submission;
    }

    public Standing.UserResult[] GetResults(bool official = true, bool notFreeze = false)
    {
        return [.. Standing.NewStanding(Contest, includeAll: false, official: official, notFreeze: notFreeze, startTime: null)];
    }
}

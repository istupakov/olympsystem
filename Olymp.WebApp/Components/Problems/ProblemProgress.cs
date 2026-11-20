using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.WebApp.Components.Problems;

public static class ProblemProgress
{
    public static IReadOnlyList<Standing.UserProblemResult> Create(IEnumerable<Problem> problems, IReadOnlyList<Submission> submissions)
    {
        var progress = Standing.UserProblemStanding(problems, submissions).ToList();
        var pending = submissions.Where(item => item.StatusCode is 0 or 1).ToLookup(item => item.ProblemId);
        foreach (var result in progress.Where(item => item.Problem.Contest.IsSchool && item.Score < item.MaxScore))
            result.UnknownCommitNumber = pending[result.Problem.Id].Count();
        return progress;
    }
}

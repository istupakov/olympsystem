using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Components.Submissions;

namespace Olymp.WebApp.Components.Staff;

public static class StaffSubmissionQuery
{
    public static IQueryable<Submission> ForAuthors(this IQueryable<Submission> query, string? scope, int userId,
        OlympContext context, int? contestId) => scope switch
        {
            "own" => query.Where(item => item.UserId == userId),
            "hidden" => query.Where(item => item.User.IsHidden),
            "participants" => query.Where(item => context.Competitors.Any(author => author.Id == item.UserId
                && author.ContestId == (contestId ?? item.Problem.ContestId))),
            _ => query
        };

    public static IQueryable<SubmissionListItem> AsStaffRows(this IQueryable<Submission> query, OlympContext context, int? contestId) =>
        query.Select(item => new SubmissionListItem(item.Id, item.CommitTime, item.User.Name,
            context.Competitors.Any(author => author.Id == item.UserId && author.ContestId == (contestId ?? item.Problem.ContestId)),
            item.Compilator.Description ?? item.Compilator.Name, item.Problem.ContestId, item.Problem.Contest.Name,
            item.ProblemId, item.Problem.Number.ToString() + ". " + item.Problem.Name, item.Problem.Contest.Abbr.StartsWith("school"),
            item.StatusCode, null, item.Score, true)
        { IsTeam = item.Problem.Contest.Abbr.StartsWith("acm") });
}

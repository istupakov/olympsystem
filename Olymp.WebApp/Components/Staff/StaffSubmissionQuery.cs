using Olymp.Domain;
using Olymp.Domain.Models;

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

    public static IQueryable<StaffSubmissionListItem> AsStaffRows(this IQueryable<Submission> query, OlympContext context, int? contestId) =>
        query.Select(item => new StaffSubmissionListItem
        {
            Id = item.Id,
            CommitTime = item.CommitTime,
            Author = item.User.Name,
            IsParticipant = context.Competitors.Any(author => author.Id == item.UserId && author.ContestId == (contestId ?? item.Problem.ContestId)),
            Compiler = item.Compilator.Description ?? item.Compilator.Name,
            ContestId = item.Problem.ContestId,
            Contest = item.Problem.Contest.Name,
            ProblemId = item.ProblemId,
            Problem = item.Problem.Number.ToString() + ". " + item.Problem.Name,
            StatusCode = item.StatusCode,
            Score = item.Score,
            IsTeam = item.Problem.Contest.Abbr.StartsWith("acm") || item.Problem.Contest.Abbr.StartsWith("icpc"),
            IpAddress = item.CommiterInfo,
            AuthorId = item.UserId,
            CompilerId = item.CompilatorId,
            Language = item.Compilator.Language,
            IsHidden = item.IsHidden,
            IsReference = item.IsReference
        });
}

using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.WebApp.Components.Contests;

public static class StandingQuery
{
    public static Task<Contest> LoadAsync(OlympContext db, int contestId, CancellationToken token = default) =>
        db.Contests.AsNoTrackingWithIdentityResolution().AsSplitQuery()
            .Where(contest => contest.Id == contestId)
            .Select(contest => new Contest
            {
                Id = contest.Id,
                Name = contest.Name,
                Abbr = contest.Abbr,
                StartTime = contest.StartTime,
                EndTime = contest.EndTime,
                IsOpen = contest.IsOpen,
                IsHidden = contest.IsHidden,
                Description = contest.Description,
                ProblemShowTime = contest.ProblemShowTime,
                FreezeTime = contest.FreezeTime,
                ProblemPdfId = contest.ProblemPdfId,
                TestZipId = contest.TestZipId,
                FinalTableId = contest.FinalTableId,
                OfficialTableId = contest.OfficialTableId,
                Messages = new List<Message>(),
                Competitors = db.Competitors.Where(team => team.ContestId == contest.Id)
                    .OrderBy(team => team.Id).Include(team => team.Members).ToList(),
                Problems = contest.Problems.OrderBy(problem => problem.Id).Select(problem => new Problem
                {
                    Id = problem.Id,
                    Number = problem.Number,
                    Name = problem.Name,
                    IsActive = problem.IsActive,
                    ContestId = problem.ContestId,
                    Contest = null!,
                    Checker = null!,
                    Messages = new List<Message>(),
                    Tests = new List<Test>(),
                    Submissions = problem.Submissions.OrderBy(submission => submission.Id).Select(submission => new Submission
                    {
                        Id = submission.Id,
                        UserId = submission.UserId,
                        User = submission.User,
                        ProblemId = submission.ProblemId,
                        CommitTime = submission.CommitTime,
                        StatusCode = submission.StatusCode,
                        Score = submission.Score,
                        IsHidden = submission.IsHidden,
                        SourceCode = Array.Empty<byte>(),
                        Problem = null!,
                        Compilator = null!,
                        CheckResults = new List<CheckResult>()
                    }).ToList()
                }).ToList()
            }).SingleAsync(token);
}

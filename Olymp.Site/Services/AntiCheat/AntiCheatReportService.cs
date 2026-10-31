using Microsoft.EntityFrameworkCore;

using Olymp.Checker;
using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Services.AntiCheat;

public sealed record PlagiarismCase(Submission Submission1, Submission Submission2, double Similarity);
public sealed record PlagiarismReport(User Competitor1, User Competitor2, IReadOnlyList<PlagiarismCase> Cases);

public sealed record AIUsingCase(Submission Submission, AIDetectionResult Result);
public sealed record AIUsingReport(User Competitor, IReadOnlyList<AIUsingCase> Cases);

/// <summary>Callers must authorize access to the contest or global analysis before requesting reports.</summary>
public sealed class AntiCheatReportService(OlympContext context, ISubmissionSimilarityService similarityService,
    IAIDetectionService aiDetectionService)
{
    public async Task<IReadOnlyList<PlagiarismReport>> FindContestPlagiarismAsync(int contestId, float threshold,
        CancellationToken token = default)
    {
        var submissions = await LoadAsync(ContestSubmissions(contestId), token);
        return Compare(submissions, submissions, threshold, token);
    }

    public async Task<IReadOnlyList<PlagiarismReport>> FindPlagiarismByDateAsync(DateTimeOffset startTime,
        DateTimeOffset endTime, float threshold, CancellationToken token = default)
    {
        var problemIds = context.Submissions
            .Where(x => x.CommitTime >= startTime && x.CommitTime <= endTime)
            .Select(x => x.ProblemId);
        var history = await LoadAsync(context.Submissions.Where(x => problemIds.Contains(x.ProblemId)), token);
        var selected = history.Where(x => x.CommitTime >= startTime && x.CommitTime <= endTime).ToArray();

        return Compare(selected, history, threshold, token);
    }

    public async Task<IReadOnlyList<PlagiarismReport>> FindPlagiarismByUserAsync(string userName, float threshold,
        CancellationToken token = default)
    {
        var user = await context.Users.OrderBy(x => x.Id)
            .FirstOrDefaultAsync(x => x.UserName == userName || x.Name == userName, token)
            ?? throw new EntityNotFoundException();

        var problemIds = context.Submissions.Where(x => x.UserId == user.Id).Select(x => x.ProblemId);
        var history = await LoadAsync(context.Submissions.Where(x => problemIds.Contains(x.ProblemId)), token);
        var selected = history.Where(x => x.UserId == user.Id).ToArray();

        return Compare(selected, history, threshold, token);
    }

    public async Task<IReadOnlyList<AIUsingReport>> DetectContestAiAsync(int contestId, float threshold,
        CancellationToken token = default)
    {
        var submissions = await LoadAsync(ContestSubmissions(contestId), token);
        var cases = new List<AIUsingCase>();
        foreach (var submission in submissions)
        {
            token.ThrowIfCancellationRequested();
            var result = aiDetectionService.DetectAI(submission.Text, submission.Compilator.Language);
            if (result.Score > threshold)
                cases.Add(new AIUsingCase(submission, result));
        }
        token.ThrowIfCancellationRequested();

        return [.. cases.GroupBy(x => x.Submission.UserId)
            .Select(group => new AIUsingReport(group.First().Submission.User, [.. group]))];
    }

    private IQueryable<Submission> ContestSubmissions(int contestId) =>
        context.Competitors.Where(x => x.ContestId == contestId).SelectMany(x => x.Submissions);

    private static Task<List<Submission>> LoadAsync(IQueryable<Submission> query, CancellationToken token) =>
        query.Include(x => x.User).Include(x => x.Problem).Include(x => x.Compilator)
            .OrderBy(x => x.Id).ToListAsync(token);

    private IReadOnlyList<PlagiarismReport> Compare(IReadOnlyList<Submission> selected,
        IReadOnlyList<Submission> history, float threshold, CancellationToken token)
    {
        var byProblem = history.ToLookup(x => x.ProblemId);
        var selectedIds = selected.Select(x => x.Id).ToHashSet();
        var cases = new List<PlagiarismCase>();

        foreach (var submission in selected)
            foreach (var other in byProblem[submission.ProblemId])
            {
                token.ThrowIfCancellationRequested();
                if (submission.UserId == other.UserId)
                    continue;

                // If both submissions are selected, compare the pair only once.
                if (selectedIds.Contains(other.Id) && submission.Id > other.Id)
                    continue;

                var similarity = similarityService.CompareSimilarity(submission.Text, other.Text);
                if (similarity > threshold)
                    cases.Add(submission.UserId < other.UserId
                        ? new PlagiarismCase(submission, other, similarity)
                        : new PlagiarismCase(other, submission, similarity));
            }
        token.ThrowIfCancellationRequested();

        return [.. cases.GroupBy(x => (x.Submission1.UserId, x.Submission2.UserId))
            .OrderBy(group => group.Key)
            .Select(group => new PlagiarismReport(group.First().Submission1.User, group.First().Submission2.User, [.. group]))];
    }
}

using System.Data;
using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Olymp.Domain.Models;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Contests;

public sealed record ProblemDeletionPreview(int Tests, int Submissions, int Checks, int Messages, bool HasParticipants, bool HasParticipantMessages)
{
    public bool CanDelete => !HasParticipants && !HasParticipantMessages;
}

public sealed partial class ContestProblemService
{
    public async Task<ProblemDeletionPreview> PreviewDeletionAsync(ClaimsPrincipal actor, int contestId, int problemId, CancellationToken token = default)
    {
        await AuthorizeAsync(actor, contestId);
        return await context.Problems.Where(p => p.Id == problemId && p.ContestId == contestId)
            .Select(p => new ProblemDeletionPreview(p.Tests.Count, p.Submissions.Count,
                p.Submissions.SelectMany(s => s.CheckResults).Count(), p.Messages.Count,
                p.Submissions.Any(s => s.User is Competitor), p.Messages.Any(m => m.User is Competitor)))
            .SingleOrDefaultAsync(token) ?? throw new EntityNotFoundException();
    }

    public async Task DeleteProblemAsync(ClaimsPrincipal actor, int contestId, int problemId, CancellationToken token = default)
    {
        using var gate = await checkerManager.EnterAsync(token);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var preview = await PreviewDeletionAsync(actor, contestId, problemId, token);
        if (!preview.CanDelete) throw new OperationValidationException(SiteResources.StaffProblemDeletionBlocked);
        if (await context.Submissions.AnyAsync(s => s.ProblemId == problemId && s.StatusCode == 1, token))
            throw new OperationValidationException(SiteResources.StaffProblemDeletionBusy);
        await context.CheckResults.Where(r => r.Submission.ProblemId == problemId).ExecuteDeleteAsync(token);
        await context.Submissions.Where(s => s.ProblemId == problemId).ExecuteDeleteAsync(token);
        await context.Tests.Where(t => t.ProblemId == problemId).ExecuteDeleteAsync(token);
        await context.Messages.Where(m => m.ProblemId == problemId).ExecuteDeleteAsync(token);
        await context.Problems.Where(p => p.Id == problemId && p.ContestId == contestId).ExecuteDeleteAsync(token);
        await transaction.CommitAsync(token);
    }
}

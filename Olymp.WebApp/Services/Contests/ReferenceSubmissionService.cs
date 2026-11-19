using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Contests;

public sealed class ReferenceSubmissionService(OlympContext db, IAuthorizationService authorization)
{
    public static bool CanAssign(Submission submission) => submission.User.IsHidden
        && submission.StatusCode is not (0 or 1) && !ReferenceSolutionFiles.IsValidator(submission.Description);

    public static string DefaultFilename(Submission submission)
    {
        string? email = submission.User.Email;
        int separator = email?.IndexOf('@') ?? -1;
        if (separator <= 0) throw new OperationValidationException(SiteResources.ReferenceEmailRequired);
        return !string.IsNullOrWhiteSpace(submission.Description) ? submission.Description
            : email![..separator] + submission.Compilator.SourceExtension;
    }

    public async Task SetAsync(ClaimsPrincipal actor, int submissionId, bool reference, string? filename,
        CancellationToken token = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token);
        var submission = await db.Submissions.AsNoTracking().Include(item => item.User).Include(item => item.Compilator)
            .Include(item => item.Problem).SingleOrDefaultAsync(item => item.Id == submissionId, token)
            ?? throw new EntityNotFoundException();
        if (!(await authorization.AuthorizeAsync(actor, submission.Problem.ContestId, PolicyNames.ImportProblems)).Succeeded)
            throw new OperationValidationException(SiteResources.StaffAccessDenied);
        if (!submission.User.IsHidden || ReferenceSolutionFiles.IsValidator(submission.Description)
            || (reference && !CanAssign(submission)))
            throw new OperationValidationException(SiteResources.ReferenceUnavailable);
        if (reference)
        {
            _ = DefaultFilename(submission);
            filename = filename?.Trim();
            if (!ReferenceSolutionFiles.IsValidPath(filename) || filename!.Contains('/'))
                throw new OperationValidationException(SiteResources.ReferenceInvalidFilename);
            if (ReferenceSolutionFiles.IsValidator(filename))
                throw new OperationValidationException(SiteResources.ReferenceReservedFilename);
            var existing = await db.Submissions.Where(item => item.ProblemId == submission.ProblemId
                && item.Id != submission.Id && item.IsReference).Include(item => item.Compilator).ToListAsync(token);
            if (existing.Any(item => ReferenceSolutionFiles.Filename(item.Id, item.Description, item.Compilator.SourceExtension)
                .Equals(filename, StringComparison.OrdinalIgnoreCase)))
                throw new OperationValidationException(SiteResources.ReferenceDuplicateFilename);
        }
        if (!(await authorization.AuthorizeAsync(actor, submission.Problem.ContestId, PolicyNames.ImportProblems)).Succeeded)
            throw new OperationValidationException(SiteResources.StaffAccessDenied);
        var query = db.Submissions.Where(item => item.Id == submissionId);
        if (reference)
            await query.ExecuteUpdateAsync(set => set.SetProperty(item => item.IsReference, true)
                .SetProperty(item => item.Description, filename), token);
        else
            await query.ExecuteUpdateAsync(set => set.SetProperty(item => item.IsReference, false), token);
        await transaction.CommitAsync(token);
    }
}

using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Components.Submissions;

public sealed class SubmissionService(IDbContextFactory<OlympContext> factory, IAuthorizationService authorization)
{
    private static readonly Encoding SourceEncoding = Encoding.GetEncoding(1251,
        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    public Task<Result> SubmitPublicAsync(ClaimsPrincipal user, int contestId, int problemId, SubmissionInput input,
        string? remoteIpAddress, CancellationToken token = default) => SubmitAsync(user, contestId, problemId, input, remoteIpAddress, jury: false, token);

    public Task<Result> SubmitJuryAsync(ClaimsPrincipal user, int contestId, int problemId, SubmissionInput input,
        CancellationToken token = default) => SubmitAsync(user, contestId, problemId, input, null, jury: true, token);

    private async Task<Result> SubmitAsync(ClaimsPrincipal user, int contestId, int problemId, SubmissionInput input,
        string? remoteIpAddress, bool jury, CancellationToken token)
    {
        if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out int userId))
            return new(null, SiteResources.SubmissionNotAllowed);

        await using var context = await factory.CreateDbContextAsync(token);
        var problem = await context.Problems.AsNoTracking().Include(item => item.Contest)
            .SingleOrDefaultAsync(item => item.Id == problemId && item.ContestId == contestId, token);
        if (problem is null)
            return new(null, null, NotFound: true);
        if (!(await authorization.AuthorizeAsync(user, problem, new SubmitSolutionRequirement { ForJury = jury })).Succeeded)
            return new(null, SiteResources.SubmissionNotAllowed);

        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(input, new ValidationContext(input), errors, validateAllProperties: true))
            return new(null, errors[0].ErrorMessage);
        if (!await context.Compilators.AnyAsync(compiler => compiler.Id == input.CompilatorId && compiler.IsActive, token))
            return new(null, SiteResources.CompilerUnavailable);

        byte[] source;
        try
        {
            source = SourceEncoding.GetBytes(input.Code);
        }
        catch (EncoderFallbackException)
        {
            return new(null, SiteResources.SourceCodeEncoding);
        }

        var now = DateTimeOffset.UtcNow;
        var submission = new Submission
        {
            User = null!,
            UserId = userId,
            Problem = null!,
            ProblemId = problem.Id,
            Compilator = null!,
            CompilatorId = input.CompilatorId!.Value,
            SourceCode = source,
            StatusCode = 0,
            CommitTime = now,
            CommiterInfo = remoteIpAddress,
            LastModification = now,
            CheckResults = []
        };
        context.Submissions.Add(submission);
        await context.SaveChangesAsync(token);
        return new(submission.Id, null);
    }

    public sealed record Result(int? Id, string? Error, bool NotFound = false);
}

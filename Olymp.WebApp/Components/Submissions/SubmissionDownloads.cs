using System.Globalization;
using System.Text;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;

namespace Olymp.WebApp.Components.Submissions;

internal static class SubmissionDownloads
{
    public static void MapSubmissionDownloads(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/submissions/{id:int}/download", DownloadAsync)
            .RequireAuthorization();
        endpoints.MapGet("/jury/submissions/{id:int}/download", DownloadStaffAsync)
            .RequireAuthorization(PolicyNames.JuryOrAdmin);
    }

    private static async Task<IResult> DownloadStaffAsync(int id, IDbContextFactory<OlympContext> factory, CancellationToken token)
    {
        await using var context = await factory.CreateDbContextAsync(token);
        var submission = await context.Submissions.AsNoTracking().Include(item => item.Compilator)
            .SingleOrDefaultAsync(item => item.Id == id, token);
        return submission is null ? TypedResults.NotFound() : SourceFile(submission);
    }

    private static async Task<IResult> DownloadAsync(int id,
        HttpContext http, IDbContextFactory<OlympContext> factory, IAuthorizationService authorization, CancellationToken token)
    {
        await using var context = await factory.CreateDbContextAsync(token);
        var submission = await context.Submissions.AsNoTracking()
            .Include(item => item.Problem).ThenInclude(item => item.Contest)
            .Include(item => item.Compilator)
            .SingleOrDefaultAsync(item => item.Id == id, token);
        if (submission is null || !(await authorization.AuthorizeAsync(http.User, submission,
            new ViewSolutionRequirement())).Succeeded)
            return Results.NotFound();

        return SourceFile(submission);
    }

    private static FileContentHttpResult SourceFile(Submission submission) =>
        TypedResults.File(Encoding.UTF8.GetBytes(submission.Text), "text/plain; charset=utf-8",
            Path.ChangeExtension(submission.Id.ToString(CultureInfo.InvariantCulture), submission.Compilator.SourceExtension));
}

using System.Globalization;
using System.Text;

using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;

namespace Olymp.WebApp.Components.Submissions;

internal static class SubmissionDownloads
{
    public static IEndpointConventionBuilder MapSubmissionDownloads(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/submissions/{id:int}/download", DownloadAsync)
            .RequireAuthorization();

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

        return Results.File(Encoding.UTF8.GetBytes(submission.Text), "text/plain; charset=utf-8",
            Path.ChangeExtension(submission.Id.ToString(CultureInfo.InvariantCulture), submission.Compilator.SourceExtension));
    }
}

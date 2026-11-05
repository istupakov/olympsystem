using System.Net.Mime;

using Microsoft.EntityFrameworkCore;

using Olymp.Domain;

namespace Olymp.WebApp.Components.Contests;

internal static class ContestDownloads
{
    public static IEndpointConventionBuilder MapContestDownloads(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/contests");
        group.MapGet("/downloadproblems/{id:int}", DownloadProblemsAsync).AllowAnonymous();
        group.MapGet("/downloadtests/{id:int}", DownloadTestsAsync).RequireAuthorization();
        return group;
    }

    private static async Task<IResult> DownloadProblemsAsync(int id, IDbContextFactory<OlympContext> factory)
    {
        await using var context = await factory.CreateDbContextAsync();
        var contest = await context.Contests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && !item.IsHidden);
        if (contest is null || !contest.AllowDownloadProblems || contest.ProblemPdfId is null)
            return Results.NotFound();

        var data = await context.Blobs.Where(blob => blob.Id == contest.ProblemPdfId).Select(blob => blob.Data).SingleAsync();

        return Results.File(data, MediaTypeNames.Application.Pdf, $"{contest.Abbr}problems.pdf");
    }

    private static async Task<IResult> DownloadTestsAsync(int id, IDbContextFactory<OlympContext> factory)
    {
        await using var context = await factory.CreateDbContextAsync();
        var contest = await context.Contests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && !item.IsHidden);
        if (contest is null || !contest.AllowDownloadTests || contest.TestZipId is null)
            return Results.NotFound();

        var data = await context.Blobs.Where(blob => blob.Id == contest.TestZipId).Select(blob => blob.Data).SingleAsync();

        return Results.File(data, MediaTypeNames.Application.Zip, $"{contest.Abbr}tests.zip");
    }
}

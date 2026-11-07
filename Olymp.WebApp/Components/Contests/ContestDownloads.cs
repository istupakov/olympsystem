using System.Net.Mime;
using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Site;

namespace Olymp.WebApp.Components.Contests;

internal static class ContestDownloads
{
    public static IEndpointConventionBuilder MapContestDownloads(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/contests");
        group.MapGet("/{id:int}/downloadproblems", (int id, HttpContext http, IDbContextFactory<OlympContext> factory) =>
            DownloadProblemsAsync(id, http, factory, forPrinting: false)).AllowAnonymous();
        group.MapGet("/{id:int}/printproblems", (int id, HttpContext http, IDbContextFactory<OlympContext> factory) =>
            DownloadProblemsAsync(id, http, factory, forPrinting: true)).RequireAuthorization(PolicyNames.Coach);
        group.MapGet("/{id:int}/downloadtests", DownloadTestsAsync).RequireAuthorization(PolicyNames.OlympUser);
        return group;
    }

    private static async Task<IResult> DownloadProblemsAsync(int id, HttpContext http, IDbContextFactory<OlympContext> factory,
        bool forPrinting)
    {
        await using var context = await factory.CreateDbContextAsync(http.RequestAborted);
        var contest = await context.Contests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && !item.IsHidden, http.RequestAborted);

        if (contest is null || contest.ProblemPdfId is null || !(forPrinting ? contest.AllowDownloadProblems : contest.AllowShowProblems))
            return Results.NotFound();

        if (!contest.AllowShowProblems)
        {
            int userId = int.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            bool allowPrint = await context.Competitors.AnyAsync(participant => participant.ContestId == id
                && participant.CoachId == userId, http.RequestAborted);
            if (!allowPrint)
                return Results.Forbid();
        }

        var data = await context.Blobs.Where(blob => blob.Id == contest.ProblemPdfId).Select(blob => blob.Data).SingleAsync(http.RequestAborted);

        return Results.File(data, MediaTypeNames.Application.Pdf, $"{contest.Abbr}problems.pdf");
    }

    private static async Task<IResult> DownloadTestsAsync(int id, HttpContext http, IDbContextFactory<OlympContext> factory)
    {
        await using var context = await factory.CreateDbContextAsync(http.RequestAborted);
        var contest = await context.Contests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && !item.IsHidden, http.RequestAborted);

        if (contest is null || contest.TestZipId is null || !contest.AllowDownloadTests)
            return Results.NotFound();

        int userId = int.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        bool related = await context.Competitors.AnyAsync(participant => participant.ContestId == id
            && (participant.CoachId == userId || participant.Members.Any(member => member.Id == userId)), http.RequestAborted);
        if (!related)
            return Results.Forbid();

        var data = await context.Blobs.Where(blob => blob.Id == contest.TestZipId).Select(blob => blob.Data).SingleAsync(http.RequestAborted);

        return Results.File(data, MediaTypeNames.Application.Zip, $"{contest.Abbr}tests.zip");
    }
}

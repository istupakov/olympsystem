using System.IO.Compression;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Components.Staff;

internal static class StaffDownloads
{
    public static void MapStaffDownloads(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/jury").RequireAuthorization(PolicyNames.JuryOrAdmin);
        group.MapGet("/problems/{problemId:int}/tests/download", DownloadZipAsync);
        group.MapGet("/problems/{problemId:int}/tests/{id:int}/input", (int problemId, int id, HttpContext http,
            IDbContextFactory<OlympContext> factory) => DownloadTestAsync(problemId, id, false, http, factory));
        group.MapGet("/problems/{problemId:int}/tests/{id:int}/output", (int problemId, int id, HttpContext http,
            IDbContextFactory<OlympContext> factory) => DownloadTestAsync(problemId, id, true, http, factory));
        group.MapGet("/contests/{contestId:int}/participants/export", ExportParticipantsAsync);
        group.MapGet("/contests/{contestId:int}/files/{kind}", DownloadContestFileAsync);
    }

    private static async Task<IResult> DownloadContestFileAsync(int contestId, string kind, HttpContext http,
        IDbContextFactory<OlympContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync(http.RequestAborted);
        var contest = await db.Contests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == contestId, http.RequestAborted);
        int? blobId = kind switch
        {
            "problems" => contest?.ProblemPdfId,
            "tests" => contest?.TestZipId,
            "final" => contest?.FinalTableId,
            "official" => contest?.OfficialTableId,
            _ => null
        };
        if (blobId is null) return Results.NotFound();
        var data = await db.Blobs.Where(item => item.Id == blobId).Select(item => item.Data).SingleOrDefaultAsync(http.RequestAborted);
        http.Response.Headers.CacheControl = "no-store";
        return data is null ? Results.NotFound() : Results.File(data, "application/octet-stream",
            $"contest-{contestId}-{kind}." + (kind == "problems" ? "pdf" : kind == "tests" ? "zip" : "html"));
    }

    private static async Task<IResult> DownloadTestAsync(int problemId, int id, bool output, HttpContext http,
        IDbContextFactory<OlympContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync(http.RequestAborted);
        var query = db.Tests.AsNoTracking().Where(item => item.ProblemId == problemId && item.Id == id);
        var test = output
            ? await query.Select(item => new { item.Number, Data = item.Output }).SingleOrDefaultAsync(http.RequestAborted)
            : await query.Select(item => new { item.Number, Data = item.Input }).SingleOrDefaultAsync(http.RequestAborted);
        http.Response.Headers.CacheControl = "no-store";
        return test is null ? Results.NotFound()
            : Results.File(test.Data, "application/octet-stream", TestQuery.FileName(test.Number, output));
    }

    private static async Task<IResult> DownloadZipAsync(int problemId, HttpContext http, IDbContextFactory<OlympContext> factory)
    {
        await using (var db = await factory.CreateDbContextAsync(http.RequestAborted))
            if (!await db.Problems.AnyAsync(item => item.Id == problemId, http.RequestAborted)) return Results.NotFound();
        http.Response.Headers.CacheControl = "no-store";
        return Results.Stream(async stream =>
        {
            await using var db = await factory.CreateDbContextAsync(http.RequestAborted);
            await using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            await foreach (var test in db.Tests.AsNoTracking().Where(item => item.ProblemId == problemId)
                .OrderBy(item => item.Number).ThenBy(item => item.Id)
                .Select(item => new { item.Number, item.Input, item.Output }).AsAsyncEnumerable().WithCancellation(http.RequestAborted))
            {
                await using (var entry = archive.CreateEntry(TestQuery.FileName(test.Number, false)).Open())
                    await entry.WriteAsync(test.Input, http.RequestAborted);
                await using (var entry = archive.CreateEntry(TestQuery.FileName(test.Number, true)).Open())
                    await entry.WriteAsync(test.Output, http.RequestAborted);
            }
        }, "application/zip", $"problem-{problemId}-tests.zip");
    }

    private static async Task<IResult> ExportParticipantsAsync(int contestId, string? search, int? organization,
        bool? approved, bool? unofficial, bool? disqualified, string? sort, bool? descending,
        int? coach, int? member, string? coachSearch, string? memberSearch,
        HttpContext http, IDbContextFactory<OlympContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync(http.RequestAborted);
        if (!await db.Contests.AnyAsync(item => item.Id == contestId, http.RequestAborted)) return Results.NotFound();
        var query = new ParticipantQuery(contestId, search, organization, approved, unofficial, disqualified, coach, member, coachSearch, memberSearch).Apply(db)
            .Where(item => item.UserName != null && item.UserName != "");
        var participants = await ParticipantQuery.Order(query, sort, descending ?? false)
            .Include(item => item.Organization).Include(item => item.Members).AsSplitQuery().ToListAsync(http.RequestAborted);
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(';', new[] { "ID", SiteResources.StaffName, SiteResources.StaffOrganization,
            SiteResources.StaffMembers, SiteResources.StaffLogin, SiteResources.StaffPassword }.Select(Escape)));
        foreach (var participant in participants)
            csv.AppendLine(string.Join(';', new[] { participant.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                participant.Name, participant.Organization?.Name,
                string.Join(", ", participant.GetMembers()?.Select(item => item.name) ?? participant.Members.Select(item => item.Name)),
                participant.UserName, participant.PasswordHash }.Select(Escape)));
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(Encoding.UTF8.Preamble.ToArray().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(),
            "text/csv; charset=utf-8", $"contest-{contestId}-logins.csv");
    }

    private static string Escape(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

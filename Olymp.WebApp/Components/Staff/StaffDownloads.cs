using System.IO.Compression;
using System.Text;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Icpc;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;
using Olymp.WebApp.Services.DatabaseMaintenance;

namespace Olymp.WebApp.Components.Staff;

internal static class StaffDownloads
{
    public static void MapStaffDownloads(this IEndpointRouteBuilder endpoints)
    {
        var jury = endpoints.MapGroup("/jury").RequireAuthorization(PolicyNames.JuryOrAdmin);
        jury.MapGet("/contests/{contestId:int}/export/download", (int contestId, [AsParameters] ContestExportOptions options, HttpContext http, ContestExportService service) =>
            ExportContestAsync(contestId, options, http, service));
        jury.MapPost("/contests/{contestId:int}/results/export", ExportIcpcResultsAsync).WithFormOptions(valueCountLimit: 20_000);
        jury.MapGet("/contests/{contestId:int}/problems/export", ExportProblemsAsync);
        jury.MapGet("/contests/{contestId:int}/statements/export", ExportStatementsAsync);
        jury.MapGet("/contests/{contestId:int}/statements/pdf", DownloadStatementPdfAsync);
        jury.MapGet("/problems/{problemId:int}/tests/download", DownloadZipAsync);
        jury.MapGet("/problems/{problemId:int}/tests/{id:int}/input", (int problemId, int id, HttpContext http,
            IDbContextFactory<OlympContext> factory) => DownloadTestAsync(problemId, id, false, http, factory));
        jury.MapGet("/problems/{problemId:int}/tests/{id:int}/output", (int problemId, int id, HttpContext http,
            IDbContextFactory<OlympContext> factory) => DownloadTestAsync(problemId, id, true, http, factory));
        jury.MapGet("/contests/{contestId:int}/participants/export", ExportParticipantsAsync);
        jury.MapGet("/contests/{contestId:int}/files/{kind}", DownloadContestFileAsync);

        var admin = endpoints.MapGroup("/admin").RequireAuthorization(PolicyNames.Admin);
        admin.MapGet("/contests/export", ([AsParameters] ContestExportOptions options, HttpContext http, ContestExportService service) =>
            ExportContestAsync(null, options, http, service));
        admin.MapGet("/backups/{name}/download", DownloadBackupAsync);
    }

    private static async Task<IResult> ExportIcpcResultsAsync(int contestId, [FromForm] IFormCollection form,
        HttpContext http, IcpcResultsService service, TimeProvider clock)
    {
        http.Response.Headers.CacheControl = "no-store";
        string format = form["format"].ToString();
        if (format is not ("csv" or "ccs")) return Results.BadRequest();
        try
        {
            var preview = await service.PreviewAsync(contestId, http.RequestAborted);
            if (preview.Snapshot != form["snapshot"].ToString()
                || (format == "csv" && preview.InvalidTeamIds.Count != 0))
                return Results.Redirect($"/jury/contests/{contestId}/export?ResultsError=changed#icpc-results");
            var awards = new Dictionary<string, IcpcResultAward>();
            foreach (var team in preview.Results.Teams)
            {
                string[] fields = [$"medal-{team.Id}", $"site-{team.Id}", $"citation-{team.Id}"];
                if (fields.Any(key => form[key].Count != 1 || form[key].ToString().Length > 1000)) return Results.BadRequest();
                awards[team.Id] = new(form[fields[0]].ToString(), form[fields[1]].ToString(), form[fields[2]].ToString());
            }
            return format == "csv"
                ? Results.File(IcpcResultsWriter.Csv(preview.Results, awards), "text/csv; charset=utf-8", $"contest-{contestId}-results.csv")
                : Results.File(IcpcResultsWriter.Ccs(preview.Results, awards, clock.GetUtcNow()), "application/zip", $"contest-{contestId}-ccs.zip");
        }
        catch (EntityNotFoundException) { return Results.NotFound(); }
        catch (OperationValidationException)
        {
            return Results.Redirect($"/jury/contests/{contestId}/export?ResultsError=changed#icpc-results");
        }
    }

    private static async Task<IResult> DownloadBackupAsync(string name, bool? single, HttpContext http, DatabaseBackupService service)
    {
        try
        {
            var files = await service.OpenDownloadAsync(http.User, name, http.RequestAborted, single ?? false);
            foreach (var file in files) http.Response.RegisterForDispose(file);
            if (files.Length == 1)
                return Results.File(files[0], "application/octet-stream", Path.GetFileName(files[0].Name));
            return Results.Stream(async stream =>
            {
                await using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var file in files)
                {
                    await using var entry = archive.CreateEntry(Path.GetFileName(file.Name), CompressionLevel.NoCompression).Open();
                    await file.CopyToAsync(entry, http.RequestAborted);
                }
            }, "application/zip", Path.GetFileNameWithoutExtension(name) + ".zip");
        }
        catch (EntityNotFoundException) { return Results.NotFound(); }
        catch (OperationValidationException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> ExportContestAsync(int? contestId, ContestExportOptions options, HttpContext http, ContestExportService service)
    {
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            var export = await service.ExportAsync(contestId, options, http.RequestAborted);
            http.Response.RegisterForDispose(export.Stream);
            return Results.File(export.Stream, "application/zip", export.FileName);
        }
        catch (EntityNotFoundException)
        {
            return Results.NotFound();
        }
        catch (OperationValidationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: SiteResources.StaffExportFailed);
        }
    }

    private static async Task<IResult> ExportProblemsAsync(int contestId, HttpContext http, ContestExportService service)
    {
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            var export = await service.ExportProblemsAsync(contestId, http.RequestAborted);
            http.Response.RegisterForDispose(export.Stream);
            return Results.File(export.Stream, "application/zip", export.FileName);
        }
        catch (EntityNotFoundException) { return Results.NotFound(); }
        catch (OperationValidationException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest, title: SiteResources.StaffExportFailed);
        }
    }

    private static async Task<IResult> DownloadStatementPdfAsync(int contestId, HttpContext http,
        ContestExportService service, StatementPdfService pdf)
    {
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            var export = await service.ExportStatementsAsync(contestId, http.RequestAborted);
            await using var archive = export.Stream;
            byte[] data = await pdf.CompileAsync(archive, http.RequestAborted);
            return Results.File(data, "application/pdf", Path.ChangeExtension(export.FileName, ".pdf"));
        }
        catch (EntityNotFoundException) { return Results.NotFound(); }
        catch (OperationValidationException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> ExportStatementsAsync(int contestId, HttpContext http, ContestExportService service)
    {
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            var export = await service.ExportStatementsAsync(contestId, http.RequestAborted);
            http.Response.RegisterForDispose(export.Stream);
            return Results.File(export.Stream, "application/zip", export.FileName);
        }
        catch (EntityNotFoundException) { return Results.NotFound(); }
        catch (OperationValidationException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest, title: SiteResources.StaffExportFailed);
        }
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
            $"{contest!.Abbr}-{kind}." + (kind == "problems" ? "pdf" : kind == "tests" ? "zip" : "html"));
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
        int? coach, int? member, string? coachSearch, string? memberSearch, bool? hasLogin, bool? hidden, bool? people,
        HttpContext http, IDbContextFactory<OlympContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync(http.RequestAborted);
        string? abbr = await db.Contests.Where(item => item.Id == contestId).Select(item => item.Abbr).SingleOrDefaultAsync(http.RequestAborted);
        if (abbr is null) return Results.NotFound();
        var query = new ParticipantQuery(contestId, search, organization, approved, unofficial, disqualified, coach, member, coachSearch, memberSearch, hasLogin, hidden).Apply(db);
        var participants = await ParticipantQuery.Order(query, sort, descending ?? false)
            .Include(item => item.Contest).Include(item => item.Organization)
            .Include(item => item.Members).ThenInclude(item => item.Organization)
            .Include(item => item.Coach).ThenInclude(item => item!.Organization)
            .AsSplitQuery().ToListAsync(http.RequestAborted);
        var csv = new StringBuilder();
        if (people == true)
        {
            csv.AppendLine(string.Join(';', new[] { SiteResources.StaffFullName, SiteResources.StaffOrganization, SiteResources.StaffPersonRole }.Select(Escape)));
            foreach (var row in ParticipantPeopleExport.Rows(participants))
                csv.AppendLine(string.Join(';', row.Select(Escape)));
        }
        else
        {
            csv.AppendLine(string.Join(';', new[] { "ID", SiteResources.StaffName, SiteResources.StaffOrganization,
                SiteResources.StaffMembers, SiteResources.StaffLogin, SiteResources.StaffPassword }.Select(Escape)));
            foreach (var participant in participants)
                csv.AppendLine(string.Join(';', new[] { participant.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    participant.Name, participant.Organization?.Name,
                    string.Join(", ", participant.GetMembers()?.Select(item => item.name) ?? participant.Members.Select(item => item.Name)),
                    participant.UserName, string.IsNullOrEmpty(participant.UserName) ? null : participant.PasswordHash }.Select(Escape)));
        }
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(Encoding.UTF8.Preamble.ToArray().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(),
            "text/csv; charset=utf-8", $"{abbr}-{(people == true ? "people" : "logins")}.csv");
    }

    private static string Escape(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

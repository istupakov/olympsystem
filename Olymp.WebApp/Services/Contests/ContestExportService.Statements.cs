using System.Data;
using System.IO.Compression;

using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Statements;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Contests;

public sealed partial class ContestExportService
{
    private static async Task WriteTexAsync(OlympContext db, ZipArchive archive, Contest contest, CancellationToken token)
    {
        var problems = await db.Problems.AsNoTracking().Where(p => p.ContestId == contest.Id && p.IsActive)
            .Include(p => p.Tests.Where(t => t.IsActive && t.IsOpen)).ToListAsync(token);
        if (!problems.Any(p => !string.IsNullOrWhiteSpace(p.Text))) return;
        await StatementTexArchive.WriteAsync(archive, contest.Abbr + "/tex/", contest.Name, contest.StartTime,
            StatementPrintService.Prepare(problems, false), SiteResources.StatementPrintWarning, token);
    }

    public async Task<ExportResult> ExportStatementsAsync(int contestId, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var contest = await db.Contests.AsNoTracking().SingleOrDefaultAsync(c => c.Id == contestId, token) ?? throw new EntityNotFoundException();
        var problems = await db.Problems.AsNoTracking().Where(p => p.ContestId == contestId && p.IsActive)
            .Include(p => p.Tests.Where(t => t.IsActive && t.IsOpen)).OrderBy(p => p.Number).ThenBy(p => p.Id).ToListAsync(token);
        var file = new FileStream(Path.Combine(Path.GetTempPath(), $"olymp-statements-{Guid.NewGuid():N}.zip"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await StatementPrintService.WriteAsync(file, contest, problems, false, token);
            await transaction.CommitAsync(token);
            file.Position = 0;
            return new(file, $"{contest.Abbr}-statements.zip");
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }
}

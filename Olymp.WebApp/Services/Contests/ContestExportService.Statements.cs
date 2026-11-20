using System.Data;

using Microsoft.EntityFrameworkCore;

using Olymp.Statements;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Contests;

public sealed partial class ContestExportService
{
    public async Task<ExportResult> ExportStatementsAsync(int contestId, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var contest = await db.Contests.AsNoTracking().SingleOrDefaultAsync(c => c.Id == contestId, token) ?? throw new EntityNotFoundException();
        if (!StatementFormat.UsesMarkdown(contest.StartTime)) throw new EntityNotFoundException();
        var problems = await db.Problems.AsNoTracking().Where(p => p.ContestId == contestId && p.IsActive)
            .Include(p => p.Tests.Where(t => t.IsActive && t.IsOpen)).OrderBy(p => p.Number).ThenBy(p => p.Id).ToListAsync(token);
        if (problems.Count == 0) throw new OperationValidationException(SiteResources.NoProblemsFound);
        var printable = problems.Select(p => new PrintableProblem(p.Number, p.Name, p.TimeLimit, p.MemoryLimit,
            p.Text is null ? null : StatementDocument.Parse(p.Text),
            [.. p.Tests.OrderBy(t => t.Number).Select(t => new StatementExample(t.Input, t.Output))])).ToArray();
        var file = new FileStream(Path.Combine(Path.GetTempPath(), $"olymp-statements-{Guid.NewGuid():N}.zip"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await StatementTexArchive.WriteAsync(file, contest.Name, contest.StartTime, printable, SiteResources.StatementPrintWarning, token);
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

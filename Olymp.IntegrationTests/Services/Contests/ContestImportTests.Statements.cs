using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Statements;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

public partial class ContestImportTests
{
    private const string Markdown = "# Новое имя\n\nОписание $n$.\n\n## Формат входных данных\n\nЧисло.\n\n## Формат выходных данных\n\nОтвет `YES`.\n";
    private static readonly int[] OmittedTestNumbers = [999];

    [Fact]
    public async Task StatementOnlyUpdateRetainsTestIdentitiesBytesFlagsReferencesAndHistory()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = await db.Contests.AsTracking().SingleAsync(c => c.Id == setup.Contest.Id, Token);
        contest.StartTime = StatementFormat.Cutoff;
        contest.EndTime = contest.StartTime.AddHours(5);
        await db.SaveChangesAsync(Token);
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var (compiler, runner) = await AddReferenceCompilerAsync(db);
        var preview = await PrepareAsync(service, setup.Actor, contest.Id,
            Package(new { number = 2, name = "Original", scores = new Dictionary<int, int> { [1] = 0, [3] = 50 }, inactiveTests = InactiveTestNumbers }, true,
                ("A/solutions/main.cpp", "// original source"u8.ToArray())));
        var row = preview.Rows[0];
        row.Solutions[0].Save = true;
        row.Solutions[0].CompilerId = compiler.Id;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        int id = await service.SaveAsync(setup.Actor, preview, row, Token);
        string initialTests = await TestSnapshotAsync(db, id);
        int initialHistory = await db.CheckResults.CountAsync(c => c.Submission.ProblemId == id, Token);
        row.TimeLimit = 2;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        await service.SaveAsync(setup.Actor, preview, row, Token);
        Assert.Equal(initialTests, await TestSnapshotAsync(db, id));
        Assert.Equal(initialHistory + 1, await db.CheckResults.CountAsync(c => c.Submission.ProblemId == id, Token));
        var stored = await db.Problems.AsTracking().Include(p => p.Tests).SingleAsync(p => p.Id == id, Token);
        stored.Tests.Single(t => t.Number == 1).Input = [0, 255, 10];
        stored.Tests.Single(t => t.Number == 3).Comment = "retained";
        await db.SaveChangesAsync(Token);
        string before = await TestSnapshotAsync(db, id);
        int history = await db.CheckResults.CountAsync(c => c.Submission.ProblemId == id, Token);
        int calls = runner.Requests.Count;
        preview = await PrepareAsync(service, setup.Actor, contest.Id, Package(new { id, name = "JSON title", openTestCount = 999, scores = new { invalid = -1 } }, false,
            ("A/statement.md", Encoding.UTF8.GetBytes(Markdown))));
        // Invalid JSON metadata is rejected before applying partial-update semantics.
        Assert.NotNull(preview.Rows[0].Source.Error);
        preview = await PrepareAsync(service, setup.Actor, contest.Id, Package(new { id, name = "JSON title", openTestCount = 999, inactiveTests = OmittedTestNumbers }, false,
            ("A/statement.md", Encoding.UTF8.GetBytes(Markdown))));
        row = preview.Rows[0];
        Assert.Empty(row.Errors);
        Assert.NotEmpty(row.Warnings);
        Assert.Equal("Новое имя", row.Parameters.Name);
        Assert.False(row.SuppliesTests);
        Assert.True(row.HasChanges);
        preview.LineEndings = ImportLineEndings.Lf;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        await service.SaveAsync(setup.Actor, preview, row, Token);
        db.ChangeTracker.Clear();
        Assert.Equal(before, await TestSnapshotAsync(db, id));
        Assert.Equal(history, await db.CheckResults.CountAsync(c => c.Submission.ProblemId == id, Token));
        Assert.Equal(calls, runner.Requests.Count);
        Assert.Equal(Markdown, await db.Problems.Where(p => p.Id == id).Select(p => p.Text).SingleAsync(Token));
        preview = await PrepareAsync(service, setup.Actor, contest.Id, Package(new { id }, false, ("A/statement.md", Encoding.UTF8.GetBytes(Markdown))));
        Assert.False(preview.Rows[0].HasChanges);
        await service.SaveAsync(setup.Actor, preview, preview.Rows[0], Token);
        Assert.Equal(history, await db.CheckResults.CountAsync(c => c.Submission.ProblemId == id, Token));
        preview = await PrepareAsync(service, setup.Actor, contest.Id, Package(new { id, name = "Ignored JSON" }, false,
            ("A/solutions/new.cpp", "// new reference"u8.ToArray())));
        row = preview.Rows[0];
        Assert.Equal("Новое имя", row.Parameters.Name);
        row.Solutions[0].Save = true;
        row.Solutions[0].CompilerId = compiler.Id;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        Assert.Single(runner.Requests.Last().Tests);
        await service.SaveAsync(setup.Actor, preview, row, Token);
        Assert.Equal(before, await TestSnapshotAsync(db, id));
    }

    [Fact]
    public async Task TitleSuppliesMissingNameAndNewTestlessDraftIsInactive()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = await db.Contests.AsTracking().SingleAsync(c => c.Id == setup.Contest.Id, Token);
        contest.StartTime = StatementFormat.Cutoff;
        await db.SaveChangesAsync(Token);
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var preview = await PrepareAsync(service, setup.Actor, contest.Id, Package(new { number = 1 }, false,
            ("A/statement.md", Encoding.UTF8.GetBytes(Markdown))));
        var row = preview.Rows[0];
        Assert.Empty(row.Errors);
        Assert.Equal("Новое имя", row.Parameters.Name);
        Assert.False(row.Proposed.IsActive);
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        int id = await service.SaveAsync(setup.Actor, preview, row, Token);
        var stale = await PrepareAsync(service, setup.Actor, contest.Id, Package(new { id }, false, ("A/statement.md", Encoding.UTF8.GetBytes(Markdown))));
        await db.Problems.Where(p => p.Id == id).ExecuteUpdateAsync(p => p.SetProperty(x => x.Text, "concurrent change"), Token);
        await Assert.ThrowsAsync<Olymp.WebApp.Services.OperationValidationException>(() => service.SaveAsync(setup.Actor, stale, stale.Rows[0], Token));
    }

    private static async Task<string> TestSnapshotAsync(OlympContext db, int id) => System.Text.Json.JsonSerializer.Serialize(
        await db.Tests.AsNoTracking().Where(t => t.ProblemId == id).OrderBy(t => t.Id)
            .Select(t => new { t.Id, t.Number, t.Input, t.Output, t.IsOpen, t.IsActive, t.Score, t.Comment }).ToListAsync(Token));
}

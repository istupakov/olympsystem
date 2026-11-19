using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

public partial class ContestImportTests
{
    [Fact]
    public async Task UnchangedImportPreservesTestIdsAndHistoryAndDetectsSelectedReferenceChanges()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var (compiler, _) = await AddReferenceCompilerAsync(db);
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id,
            Package(new { number = 1, name = "Unchanged" }, true, ("A/solutions/main.cpp", "// original source"u8.ToArray())));
        var row = preview.Rows[0];
        row.Solutions[0].Save = true;
        row.Solutions[0].CompilerId = compiler.Id;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        int id = await service.SaveAsync(setup.Actor, preview, row, Token);
        int[] testIds = await db.Tests.Where(t => t.ProblemId == id).OrderBy(t => t.Id).Select(t => t.Id).ToArrayAsync(Token);
        int historyCount = await db.CheckResults.CountAsync(r => r.Submission.ProblemId == id, Token);
        preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id,
            Package(new { id }, true, ("A/solutions/other.cpp", "// new source"u8.ToArray())));
        row = preview.Rows[0];
        Assert.False(row.HasChanges);
        Assert.False(row.IsSaved);
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        Assert.Equal(id, await service.SaveAsync(setup.Actor, preview, row, Token));
        Assert.Equal(testIds, await db.Tests.Where(t => t.ProblemId == id).OrderBy(t => t.Id).Select(t => t.Id).ToArrayAsync(Token));
        Assert.Equal(historyCount, await db.CheckResults.CountAsync(r => r.Submission.ProblemId == id, Token));
        row.TimeLimit++;
        Assert.True(row.HasChanges);
        row.TimeLimit--;
        Assert.False(row.HasChanges);
        row.Solutions[0].Save = true;
        Assert.True(row.HasChanges);
        row.Solutions[0].Save = false;
        Assert.False(row.HasChanges);
        preview.LineEndings = ImportLineEndings.Lf;
        Assert.True(row.HasChanges);
        preview.LineEndings = ImportLineEndings.Crlf;
        Assert.False(row.HasChanges);
        foreach (object manifest in new object[]
        {
            new { id, openTestCount = 0 }, new { id, inactiveTests = InactiveTestNumbers },
            new { id, scores = new Dictionary<int, int> { [1] = 0 } },
            new { id, testComments = new Dictionary<int, string> { [1] = "comment" } }
        })
            Assert.True((await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(manifest))).Rows[0].HasChanges);
        preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id,
            Package(new { id }, true, ("A/validator.cpp", "// validator"u8.ToArray())));
        row = preview.Rows[0];
        Assert.False(row.HasChanges);
        row.Validator!.Save = true;
        Assert.True(row.HasChanges);
    }
}

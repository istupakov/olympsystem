using System.IO.Compression;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Statements;
using Olymp.Tests.Shared;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

public partial class ContestExportTests
{
    [Fact]
    public async Task MarkdownExportsRetainSourceAndPrintableArchiveIncludesOnlyPublicActiveExamples()
    {
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(contest);
        contest.StartTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.FromHours(7));
        var problem = TestEntities.CreateProblem(contest, 2);
        problem.Text = "# Русское условие\n\nСимвол `0`, ответ `YES`, $n \\le 10^9$.\n\n## Формат входных данных\n\nЧисло.\n\n## Формат выходных данных\n\nОтвет.\n";
        problem.Tests.Add(new Test { Problem = problem, Number = 1, IsActive = true, IsOpen = true, Input = "1\n2\n"u8.ToArray(), Output = "YES\n"u8.ToArray() });
        problem.Tests.Add(new Test { Problem = problem, Number = 2, IsActive = false, IsOpen = true, Input = "inactive"u8.ToArray(), Output = [] });
        problem.Tests.Add(new Test { Problem = problem, Number = 3, IsActive = true, IsOpen = false, Input = "secret"u8.ToArray(), Output = [] });
        var missing = TestEntities.CreateProblem(contest, 7);
        var inactive = TestEntities.CreateProblem(contest, 8);
        inactive.IsActive = false;
        db.Problems.AddRange(problem, missing, inactive);
        await db.SaveChangesAsync(token);
        var service = scope.ServiceProvider.GetRequiredService<ContestExportService>();
        foreach (bool simplified in new[] { false, true })
        {
            var export = simplified ? await service.ExportProblemsAsync(contest.Id, token) : await service.ExportAsync(contest.Id, token);
            await using var stream = export.Stream;
            var imported = await ProblemArchive.ReadAsync(stream, new(), token);
            Assert.Equal(problem.Text, imported.Problems.Single(p => p.Manifest.Id == problem.Id).Statement);
        }
        var printable = await service.ExportStatementsAsync(contest.Id, token);
        await using var texStream = printable.Stream;
        using var archive = new ZipArchive(texStream);
        Assert.Equal(2, archive.Entries.Count(e => e.Name.StartsWith("problem", StringComparison.Ordinal) && e.Name.EndsWith(".tex", StringComparison.Ordinal)));
        Assert.Equal(2, archive.Entries.Count(e => e.FullName.StartsWith("samples/", StringComparison.Ordinal)));
        using var input = new StreamReader(archive.GetEntry("samples/problem00-1.in")!.Open(), Encoding.UTF8);
        Assert.Equal("1\n2\n", await input.ReadToEndAsync(token));
        using var warnings = new StreamReader(archive.GetEntry("warnings.txt")!.Open(), Encoding.UTF8);
        Assert.Contains("7.", await warnings.ReadToEndAsync(token));
    }
}

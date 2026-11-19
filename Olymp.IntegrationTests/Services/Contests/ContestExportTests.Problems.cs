using System.IO.Compression;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

public partial class ContestExportTests
{
    [Fact]
    public async Task SimplifiedArchiveRetainsIdsMetadataRawTestsAndOnlyReferenceSources()
    {
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        var author = await site.AddUserAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(contest);
        db.Attach(author);
        var problem = TestEntities.CreateProblem(contest);
        problem.Text = "<p>Not included</p>";
        problem.TimeLimit = 3;
        problem.SlowTimeLimit = 8;
        problem.Tests.Add(new Test { Problem = problem, Number = 2, Input = [255, 13, 10, 0], Output = [128, 10], IsOpen = true, IsActive = true, Score = 0, Comment = "First" });
        problem.Tests.Add(new Test { Problem = problem, Number = 5, Input = [1, 13], Output = [2, 0], IsActive = false, Score = 10, Comment = "Inactive" });
        var inactive = TestEntities.CreateProblem(contest, 2);
        inactive.IsActive = false;
        var compiler = TestEntities.CreateCompilator();
        string referenceSource = "// Эталонное решение\r\nint main() {}";
        var reference = TestEntities.CreateSubmission(author, problem, compiler, DateTimeOffset.UtcNow, Encoding.GetEncoding(1251).GetBytes(referenceSource));
        reference.IsReference = true;
        reference.Description = "main.cpp";
        var validator = TestEntities.CreateSubmission(author, problem, compiler, DateTimeOffset.UtcNow, "// validator source"u8.ToArray());
        validator.IsReference = true;
        validator.Description = "validator.cpp";
        var ordinary = TestEntities.CreateSubmission(author, problem, compiler, DateTimeOffset.UtcNow, "// ordinary source"u8.ToArray());
        ordinary.Description = "ordinary.cpp";
        db.Problems.Add(inactive);
        db.Submissions.AddRange(reference, validator, ordinary);
        await db.SaveChangesAsync(token);
        var export = await scope.ServiceProvider.GetRequiredService<ContestExportService>().ExportProblemsAsync(contest.Id, token);
        await using var stream = export.Stream;
        Assert.Equal($"{contest.Abbr}-problems.zip", export.FileName);
        var imported = await ProblemArchive.ReadAsync(stream, new(), token);
        Assert.Null(imported.ContestId);
        Assert.Equal(2, imported.Problems.Count);
        var first = imported.Problems.Single(p => p.Manifest.Id == problem.Id);
        Assert.Null(first.Error);
        Assert.Equal("problem01", first.Folder);
        Assert.Equal(problem.Name, first.Manifest.Name);
        Assert.Equal(3, first.Manifest.TimeLimit);
        Assert.Equal(8, first.Manifest.SlowTimeLimit);
        Assert.Equal(problem.CheckerId, first.Manifest.CheckerId);
        Assert.Equal(problem.MemoryLimit, first.Manifest.MemoryLimit);
        Assert.Equal(1, first.Manifest.OpenTestCount);
        Assert.Equal([5], first.Manifest.InactiveTests!);
        Assert.Equal(0, first.Manifest.Scores![2]);
        Assert.Equal("Inactive", first.Manifest.TestComments![5]);
        Assert.Equal([255, 13, 10, 0], first.Tests[0].Input);
        Assert.Equal([128, 10], first.Tests[0].Output);
        Assert.Equal(referenceSource, Assert.Single(first.Solutions).Source);
        Assert.Equal("main.cpp", first.Solutions[0].Filename);
        Assert.Equal("// validator source", first.Validator);
        Assert.False(imported.Problems.Single(p => p.Manifest.Id == inactive.Id).Manifest.IsActive);
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(8, zip.Entries.Count);
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("statement", StringComparison.Ordinal)
            || entry.FullName.Contains("jury-solutions", StringComparison.Ordinal) || entry.FullName.EndsWith(".jsonl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimplifiedExportRejectsNonPrefixOpenTestsAndUnsupportedNumbers(bool unsupportedNumber)
    {
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(contest);
        var problem = TestEntities.CreateProblem(contest);
        problem.Tests.Add(new Test { Problem = problem, Number = 1, Input = [], Output = [], IsOpen = false });
        problem.Tests.Add(new Test { Problem = problem, Number = unsupportedNumber ? 1000 : 2, Input = [], Output = [], IsOpen = !unsupportedNumber });
        db.Problems.Add(problem);
        await db.SaveChangesAsync(token);
        var service = scope.ServiceProvider.GetRequiredService<ContestExportService>();
        var exception = await Assert.ThrowsAsync<OperationValidationException>(() => service.ExportProblemsAsync(contest.Id, token));
        Assert.Contains(problem.Name, exception.Message);
        var full = await service.ExportAsync(contest.Id, token);
        await full.Stream.DisposeAsync();
    }
}

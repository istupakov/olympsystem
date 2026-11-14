using System.IO.Compression;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

[Collection("Contest management")]
public class ContestMaterialsTests(ContestServiceFixture site) : IClassFixture<ContestServiceFixture>
{
    [Fact]
    public async Task ImportPreservesProblemIdentitySubmissionsOtherProblemsAndBuildsManagerArchive()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using (var setup = site.Services.CreateScope())
            await ImportAsync(setup.ServiceProvider, contest.Id, ProblemArchives.ProblemZip());
        int id;
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var problem = await db.Problems.Include(x => x.Tests).SingleAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken);
            id = problem.Id;
            Assert.Equal([0, 100], problem.Tests.OrderBy(x => x.Number).Select(x => x.Score));
            Assert.True(problem.Tests.Single(x => x.Number == 1).IsOpen);
            await ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.ProblemZip(2, "Другая"));
            await AddSubmissionsAsync(db, owner.Id, problem, [2]);
        }
        using (var update = site.Services.CreateScope())
            await ImportAsync(update.ServiceProvider, contest.Id,
                ProblemArchives.ProblemZip(name: "Новое имя", openTestCount: 0, scores: new() { [1] = 30, [2] = 70 }));
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var problem = await db.Problems.Include(x => x.Tests).SingleAsync(x => x.Id == id, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("Новое имя", problem.Name);
            Assert.Equal(2, await db.Problems.CountAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.Submissions.CountAsync(x => x.ProblemId == id, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal([30, 70], problem.Tests.OrderBy(x => x.Number).Select(x => x.Score));
            // Inactive tests must not enter the downloadable archive.
            await db.Tests.Where(x => x.ProblemId == id && x.Number == 2).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), cancellationToken: TestContext.Current.CancellationToken);
        }
        using var generation = site.Services.CreateScope();
        await generation.ServiceProvider.GetRequiredService<ContestProblemService>().GenerateTestZipAsync(contest.Id, TestContext.Current.CancellationToken);
        var archive = await generation.ServiceProvider.GetRequiredService<OlympContext>().Contests
            .Where(item => item.Id == contest.Id).Select(item => item.TestZip!.Data).SingleAsync(TestContext.Current.CancellationToken);
        using var memory = new MemoryStream(archive);
        using var zip = new ZipArchive(memory);
        var entry = zip.GetEntry($"Test{contest.Abbr}/1. Новое имя/input001.txt");
        Assert.NotNull(entry);
        Assert.NotNull(zip.GetEntry($"Test{contest.Abbr}/1. Новое имя/output001.txt"));
        using var input = entry.Open();
        using var bytes = new MemoryStream();
        await input.CopyToAsync(bytes, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0, 255, 13, 10 }, bytes.ToArray());
        Assert.Null(zip.GetEntry($"Test{contest.Abbr}/1. Новое имя/input002.txt"));
        Assert.Null(zip.GetEntry($"Test{contest.Abbr}/1. Новое имя/output002.txt"));
    }

    [Fact]
    public async Task EmptyGenerationAndInvalidImportKeepPreviousArchiveAndProblem()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.ProblemZip());
        var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
        await service.GenerateTestZipAsync(contest.Id, TestContext.Current.CancellationToken);
        var old = await db.Contests.Where(x => x.Id == contest.Id).Select(x => x.TestZip!.Data).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var invalid = ProblemArchives.Zip(
            ("A/problem.json", "{\"number\":1,\"name\":\"Не сохранять\",\"openTestCount\":0}"u8.ToArray()),
            ("A/input1.txt", [1]), ("A/output1.txt", [2]),
            ("B/problem.json", "{\"number\":2,\"name\":\"Ошибка\",\"openTestCount\":0,\"checkerId\":999}"u8.ToArray()),
            ("B/input1.txt", [3]), ("B/output1.txt", [4]));
        await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(scope.ServiceProvider, contest.Id, invalid));
        db.ChangeTracker.Clear();
        Assert.Equal("Задача", await db.Problems.Where(x => x.ContestId == contest.Id).Select(x => x.Name).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await db.Tests.CountAsync(x => x.Problem.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken));
        await db.Problems.Where(x => x.ContestId == contest.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GenerateTestZipAsync(contest.Id, TestContext.Current.CancellationToken));
        db.ChangeTracker.Clear();
        Assert.Equal(old, await db.Contests.Where(x => x.Id == contest.Id).Select(x => x.TestZip!.Data).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    internal static async Task ImportAsync(IServiceProvider provider, int contestId, byte[] zip)
    {
        using var stream = new MemoryStream(zip);
        await provider.GetRequiredService<ContestProblemService>().ImportAsync(contestId, stream);
    }

    internal static async Task AddSubmissionsAsync(OlympContext db, int userId, Problem problem, int[] statuses)
    {
        var compilator = new Compilator
        {
            Name = "test",
            Description = "Test compiler",
            Language = "C",
            SourceExtension = ".c",
            CommandLine = "compile",
            ConfigName = "test",
            IsActive = true,
            Submissions = []
        };
        db.Compilators.Add(compilator);
        foreach (var status in statuses)
            db.Submissions.Add(new Submission
            {
                ProblemId = problem.Id,
                Problem = null!,
                UserId = userId,
                User = null!,
                Compilator = compilator,
                SourceCode = "test"u8.ToArray(),
                StatusCode = status,
                Score = status == 2 ? 100 : 25,
                ScoresByTest = "25;0",
                CommitTime = DateTimeOffset.Now,
                LastModification = DateTimeOffset.Now,
                CheckResults = [new CheckResult { Submission = null!, StatusCode = status, CheckTime = DateTimeOffset.Now, Log = "Old check" }]
            });
        await db.SaveChangesAsync();
    }

}

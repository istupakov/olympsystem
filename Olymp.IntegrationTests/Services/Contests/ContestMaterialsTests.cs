using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Services;
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
            Assert.All(problem.Tests, test => Assert.Null(test.Score));
            Assert.True(problem.Tests.Single(x => x.Number == 1).IsOpen);
            await ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.ProblemZip(2, "Другая"));
            await AddSubmissionsAsync(db, owner.Id, problem, [2]);
        }
        using (var update = site.Services.CreateScope())
            await ImportAsync(update.ServiceProvider, contest.Id,
                ProblemArchives.ProblemZip(name: "Новое имя", openTestCount: 0, scores: new() { [1] = 30, [2] = 70 }, id: id));
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
    public async Task InvalidProblemDoesNotRollBackAnotherSavedProblemOrReplacePublishedArchive()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.ProblemZip());
        var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
        await service.GenerateTestZipAsync(contest.Id, TestContext.Current.CancellationToken);
        var old = await db.Contests.Where(x => x.Id == contest.Id).Select(x => x.TestZip!.Data).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        int problemId = await db.Problems.Where(p => p.ContestId == contest.Id).Select(p => p.Id).SingleAsync(TestContext.Current.CancellationToken);
        var invalid = ProblemArchives.Zip(
            ("A/problem.json", JsonSerializer.SerializeToUtf8Bytes(new { id = problemId, name = "Сохранено", openTestCount = 0 })),
            ("A/tests/input1.txt", [1]), ("A/tests/output1.txt", [2]),
            ("B/problem.json", "{\"number\":2,\"name\":\"Ошибка\",\"openTestCount\":0,\"checkerId\":999}"u8.ToArray()),
            ("B/tests/input1.txt", [3]), ("B/tests/output1.txt", [4]));
        await Assert.ThrowsAsync<OperationValidationException>(() => ImportAsync(scope.ServiceProvider, contest.Id, invalid));
        db.ChangeTracker.Clear();
        Assert.Equal("Сохранено", await db.Problems.Where(x => x.ContestId == contest.Id).Select(x => x.Name).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.Tests.CountAsync(x => x.Problem.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken));
        await db.Problems.Where(x => x.ContestId == contest.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GenerateTestZipAsync(contest.Id, TestContext.Current.CancellationToken));
        db.ChangeTracker.Clear();
        Assert.Equal(old, await db.Contests.Where(x => x.Id == contest.Id).Select(x => x.TestZip!.Data).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    internal static async Task ImportAsync(IServiceProvider provider, int contestId, byte[] zip)
    {
        using var stream = new MemoryStream(zip);
        var db = provider.GetRequiredService<OlympContext>();
        var ownerId = await db.UserClaims.Where(c => c.ClaimType == ClaimNames.ContestManage && c.ClaimValue == contestId.ToString()).Select(c => c.UserId).FirstAsync(TestContext.Current.CancellationToken);
        var actor = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, ownerId.ToString()), new(ClaimTypes.Role, RoleNames.Jury)], "Test"));
        var service = provider.GetRequiredService<ContestImportService>();
        var preview = await service.PrepareAsync(actor, contestId, stream, TestContext.Current.CancellationToken);
        foreach (var row in preview.Rows)
        {
            await service.EnsureValidatorAsync(actor, preview, row, TestContext.Current.CancellationToken);
            await service.SaveAsync(actor, preview, row, TestContext.Current.CancellationToken);
        }
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

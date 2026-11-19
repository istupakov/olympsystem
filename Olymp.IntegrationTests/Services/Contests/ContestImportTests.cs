using System.Security.Claims;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Checker;
using Olymp.Checker.Compilators;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Checker;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

[Collection("Contest management")]
public partial class ContestImportTests(ContestServiceFixture site) : IClassFixture<ContestServiceFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly int[] ActiveTestNumbers = [1];
    private static readonly int[] InactiveTestNumbers = [3];
    private static readonly int[] ProblemNumbers = [1, 2];

    private static ClaimsPrincipal Actor(OlympUser user) => new(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Role, RoleNames.Jury)], "Test"));

    private async Task<(ClaimsPrincipal Actor, Contest Contest)> SetupAsync()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        return (Actor(owner), await site.AddContestAsync(owner));
    }
    private static async Task<ContestImportPreview> PrepareAsync(ContestImportService service, ClaimsPrincipal actor, int contestId, byte[] zip)
    {
        using var stream = new MemoryStream(zip);
        return await service.PrepareAsync(actor, contestId, stream, Token);
    }
    private static byte[] Package(object manifest, bool tests = true, params (string Name, byte[] Data)[] extra) =>
        ProblemArchives.Zip([("A/problem.json", JsonSerializer.SerializeToUtf8Bytes(manifest)),
            .. tests ? new (string, byte[])[] { ("A/tests/input01.txt", [0, 255, 13, 10]), ("A/tests/output01.txt", [2]), ("A/tests/input03.txt", [3]), ("A/tests/output03.txt", [4]) } : [], .. extra]);

    [Fact]
    public async Task IdentityMatchingAndOmittedParametersPreserveHistoryWhileTestsAreReplaced()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { number = 0, name = "Original", timeLimit = 3, slowTimeLimit = 7, scores = new Dictionary<int, int> { [1] = 0 }, inactiveTests = InactiveTestNumbers, testComments = new { } }));
        await service.EnsureValidatorAsync(setup.Actor, preview, preview.Rows[0], Token);
        await service.SaveAsync(setup.Actor, preview, preview.Rows[0], Token);
        var original = await db.Problems.Include(p => p.Tests).SingleAsync(p => p.ContestId == setup.Contest.Id, Token);
        Assert.Equal([0, null], original.Tests.OrderBy(t => t.Number).Select(t => t.Score));
        Assert.False(original.Tests.Single(t => t.Number == 3).IsActive);
        await ContestMaterialsTests.AddSubmissionsAsync(db, int.Parse(setup.Actor.FindFirstValue(ClaimTypes.NameIdentifier)!), original, [2]);
        preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { id = original.Id, name = "Updated", number = 2, testComments = new Dictionary<int, string> { [3] = "comment" } }));
        var row = Assert.Single(preview.Rows);
        Assert.Equal(3, row.TimeLimit); Assert.Equal(7, row.SlowTimeLimit);
        row.TimeLimit = 5; row.SlowTimeLimit = 9;
        await service.EnsureValidatorAsync(setup.Actor, preview, preview.Rows[0], Token);
        await service.SaveAsync(setup.Actor, preview, preview.Rows[0], Token);
        db.ChangeTracker.Clear();
        var updated = await db.Problems.Include(p => p.Tests).SingleAsync(p => p.Id == original.Id, Token);
        Assert.Equal("Updated", updated.Name); Assert.Equal(2, updated.Number);
        Assert.Equal(5, updated.TimeLimit); Assert.Equal(9, updated.SlowTimeLimit);
        Assert.All(updated.Tests, t => Assert.Null(t.Score));
        Assert.Equal("comment", updated.Tests.Single(t => t.Number == 3).Comment);
        Assert.Equal(1, await db.Submissions.CountAsync(s => s.ProblemId == original.Id, Token));
        Assert.Equal(1, await db.CheckResults.CountAsync(s => s.Submission.ProblemId == original.Id, Token));
    }

    [Fact]
    public async Task ProblemsAreSortedAndSavedIndependentlyWithDuplicateNumbersAsWarnings()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await ContestMaterialsTests.ImportAsync(scope.ServiceProvider, setup.Contest.Id, ProblemArchives.ProblemZip());
        var original = await db.Problems.SingleAsync(p => p.ContestId == setup.Contest.Id, Token);
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, ProblemArchives.Zip(
            ("A/problem.json", JsonSerializer.SerializeToUtf8Bytes(new { id = original.Id, number = 2, isActive = false })),
            ("B/problem.json", "{\"number\":1,\"name\":\"New\",\"isActive\":false}"u8.ToArray())));
        Assert.Equal(ProblemNumbers, preview.Rows.Select(r => r.Parameters.Number));
        var added = preview.Rows[0];
        var updated = preview.Rows[1];
        await service.EnsureValidatorAsync(setup.Actor, preview, added, Token);
        Assert.Contains(SiteResources.ImportDuplicateNumber, added.Warnings);
        Assert.Empty(added.Errors);
        int addedId = await service.SaveAsync(setup.Actor, preview, added, Token);
        Assert.True(added.IsSaved);
        Assert.False(updated.IsSaved);
        Assert.Equal(1, await db.Problems.Where(p => p.Id == original.Id).Select(p => p.Number).SingleAsync(Token));
        added.TimeLimit = 3;
        await service.SaveAsync(setup.Actor, preview, added, Token);
        Assert.Equal(addedId, added.Id);
        Assert.Equal(2, await db.Problems.CountAsync(p => p.ContestId == setup.Contest.Id, Token));
        await service.EnsureValidatorAsync(setup.Actor, preview, updated, Token);
        Assert.Equal(original.Id, await service.SaveAsync(setup.Actor, preview, updated, Token));
        Assert.Equal(ProblemNumbers, await db.Problems.Where(p => p.ContestId == setup.Contest.Id).OrderBy(p => p.Number).Select(p => p.Number).ToArrayAsync(Token));
        Assert.Empty(await db.Tests.Where(t => t.ProblemId == original.Id).ToArrayAsync(Token));
    }

    [Theory]
    [InlineData("{\"id\":2147483647}")]
    [InlineData("{\"number\":1,\"name\":\"Empty active\"}")]
    [InlineData("{\"number\":1,\"name\":\"Bad metadata\",\"isActive\":false,\"scores\":{\"1\":0}}")]
    [InlineData("{\"number\":1,\"name\":\"Bad open tests\",\"isActive\":false,\"openTests\":[]}")]
    public async Task BlockingErrorsPreventAnyWrites(string json)
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, ProblemArchives.Zip(("A/problem.json", Encoding.UTF8.GetBytes(json))));
        await service.EnsureValidatorAsync(setup.Actor, preview, preview.Rows[0], Token);
        Assert.NotEmpty(Assert.Single(preview.Rows).Errors);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(setup.Actor, preview, preview.Rows[0], Token));
        Assert.False(await scope.ServiceProvider.GetRequiredService<OlympContext>().Problems.AnyAsync(p => p.ContestId == setup.Contest.Id, Token));
    }

    [Fact]
    public async Task ForeignContestIdAndProblemIdAreRejectedAndOmittedIdNeverMatchesByNumber()
    {
        var setup = await SetupAsync();
        var other = await site.AddContestAsync();
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await ContestMaterialsTests.ImportAsync(scope.ServiceProvider, setup.Contest.Id, ProblemArchives.ProblemZip());
        var problem = await db.Problems.SingleAsync(p => p.ContestId == setup.Contest.Id, Token);
        db.UserClaims.Add(new() { UserId = int.Parse(setup.Actor.FindFirstValue(ClaimTypes.NameIdentifier)!), ClaimType = ClaimNames.ContestManage, ClaimValue = other.Id.ToString() });
        await db.SaveChangesAsync(Token);
        var foreign = await PrepareAsync(service, setup.Actor, other.Id, Package(new { id = problem.Id, isActive = false }, false));
        await service.EnsureValidatorAsync(setup.Actor, foreign, foreign.Rows[0], Token);
        Assert.Contains(SiteResources.ImportUnknownId, foreign.Rows[0].Errors);
        var duplicate = await PrepareAsync(service, setup.Actor, setup.Contest.Id, ProblemArchives.ProblemZip());
        await service.EnsureValidatorAsync(setup.Actor, duplicate, duplicate.Rows[0], Token);
        Assert.Contains(SiteResources.ImportDuplicateNumber, duplicate.Rows[0].Warnings);
        Assert.Empty(duplicate.Rows[0].Errors);
        await Assert.ThrowsAsync<OperationValidationException>(() => PrepareAsync(service, setup.Actor, setup.Contest.Id,
            ProblemArchives.Zip(("contest/contest.json", JsonSerializer.SerializeToUtf8Bytes(new { id = other.Id })),
                ("contest/A/problem.json", "{}"u8.ToArray()))));
    }

    [Fact]
    public async Task StaleDataRequiresRefreshAndPermissionsAreRecheckedWhenSaving()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await ContestMaterialsTests.ImportAsync(scope.ServiceProvider, setup.Contest.Id, ProblemArchives.ProblemZip());
        var p = await db.Problems.SingleAsync(p => p.ContestId == setup.Contest.Id, Token);
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { id = p.Id }));
        await service.EnsureValidatorAsync(setup.Actor, preview, preview.Rows[0], Token);
        preview.Rows[0].SlowTimeLimit++;
        Assert.Equal(p.Id, await service.SaveAsync(setup.Actor, preview, preview.Rows[0], Token));
        await db.Tests.Where(t => t.ProblemId == p.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Comment, "Concurrent"), Token);
        Assert.Equal(SiteResources.ImportStale, (await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(setup.Actor, preview, preview.Rows[0], Token))).Message);
        await service.RefreshAsync(setup.Actor, preview, preview.Rows[0], Token);
        await service.EnsureValidatorAsync(setup.Actor, preview, preview.Rows[0], Token);
        await db.UserClaims.Where(c => c.ClaimType == ClaimNames.ContestManage && c.ClaimValue == setup.Contest.Id.ToString()).ExecuteDeleteAsync(Token);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(setup.Actor, preview, preview.Rows[0], Token));
    }

    [Fact]
    public async Task ImportWaitsForCurrentJudgingAndCancellationLeavesTestsUntouched()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await ContestMaterialsTests.ImportAsync(scope.ServiceProvider, setup.Contest.Id, ProblemArchives.ProblemZip());
        var p = await db.Problems.SingleAsync(p => p.ContestId == setup.Contest.Id, Token);
        await ContestMaterialsTests.AddSubmissionsAsync(db, int.Parse(setup.Actor.FindFirstValue(ClaimTypes.NameIdentifier)!), p, [1]);
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { id = p.Id, name = "After judging" }));
        await service.EnsureValidatorAsync(setup.Actor, preview, preview.Rows[0], Token);
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token))
        {
            cancel.CancelAfter(200);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(setup.Actor, preview, preview.Rows[0], cancel.Token));
        }
        Assert.Equal(2, await db.Tests.CountAsync(t => t.ProblemId == p.Id, Token));
        var apply = service.SaveAsync(setup.Actor, preview, preview.Rows[0], Token);
        using (var completion = site.Services.CreateScope())
            await completion.ServiceProvider.GetRequiredService<OlympContext>().Submissions.Where(s => s.ProblemId == p.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.StatusCode, 2), Token);
        Assert.Equal(p.Id, await apply.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal("After judging", await db.Problems.Where(x => x.Id == p.Id).Select(x => x.Name).SingleAsync(Token));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidatorRunsOnceAndBlocksOnlyActiveFailuresWhileSolutionsAreIndependent(bool inactive)
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var info = new GccCpp23Configuration().ParseInfo("14.2.0") with { FullName = Guid.NewGuid().ToString("N") };
        var compiler = new Compilator
        {
            Name = info.Configuration.Name,
            Description = info.FullName,
            Language = "C++",
            ConfigName = info.Configuration.ConfigName,
            SourceExtension = info.Configuration.SourceExtension,
            CommandLine = info.Configuration.CompileCommand,
            IsActive = true,
            Submissions = []
        };
        db.Compilators.Add(compiler);
        await db.SaveChangesAsync(Token);
        var runner = new ImportChecker(info);
        var manager = (CheckerManager)site.Services.GetRequiredService<ICheckerManager>();
        manager.Register(runner, Token);
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id,
            Package(new { number = 1, name = "Checked", inactiveTests = inactive ? InactiveTestNumbers : [], scores = new Dictionary<int, int> { [3] = 100 } }, true,
                ("A/validator.cpp", "validator"u8.ToArray()), ("A/solutions/main.cpp", "solution"u8.ToArray())));
        preview.ValidatorCompilerId = compiler.Id;
        await service.EnsureValidatorAsync(setup.Actor, preview, preview.Rows[0], Token);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(inactive, row.Errors.Count == 0);
        if (inactive) Assert.NotEmpty(row.Warnings);
        Assert.Empty(runner.Requests);
        Assert.Equal(1, runner.ValidatorCalls);
        row.Solutions[0].Selected = true;
        row.Solutions[0].CompilerId = compiler.Id;
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        var original = Assert.Single(runner.Requests);
        Assert.Equal(inactive ? ActiveTestNumbers : [1, 3], original.Tests.Select(t => t.Number));
        row.TimeLimit = 5; row.SlowTimeLimit = 9;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RefreshAsync(setup.Actor, preview, row, Token);
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        Assert.Equal(1, runner.ValidatorCalls);
        preview.ValidatorCompilerId = null;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        Assert.Null(row.ValidatorResult);
        preview.ValidatorCompilerId = compiler.Id;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        Assert.Equal(2, runner.ValidatorCalls);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        Assert.Equal(TimeSpan.FromSeconds(5), runner.Requests.Last().TimeLimit);
        Assert.Equal(5, row.TimeLimit); Assert.Equal(9, row.SlowTimeLimit);
        Assert.Equal([5], row.Runs.Where(r => r.File == "main.cpp" && r.Compiler == info.FullName).Select(r => r.TimeLimit));
        // Informational solution runs cannot override a failed validator.
        if (!inactive) await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(setup.Actor, preview, preview.Rows[0], Token));
        else
        {
            await service.SaveAsync(setup.Actor, preview, preview.Rows[0], Token);
            Assert.Equal(5, await db.Problems.Where(p => p.ContestId == setup.Contest.Id).Select(p => p.TimeLimit).SingleAsync(Token));
        }
        Assert.False(await db.Submissions.AnyAsync(s => s.Problem.ContestId == setup.Contest.Id, Token));
    }

    [Fact]
    public async Task SolutionsUseOnlySelectedDatabaseCompilersAndTheirRegularOrSlowLimit()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var manager = (CheckerManager)site.Services.GetRequiredService<ICheckerManager>();
        var runners = new List<ImportChecker>();
        var compilers = new List<Compilator>();
        foreach (var config in new ICompilatorConfiguration[] { new GccCpp23Configuration(), new CPythonConfiguration(), new PyPyConfiguration() })
        {
            var info = config.ParseInfo(config.Language == "C++" ? "14.2.0" : config.Name == "PyPy" ? "Python 3.11.0\nPyPy 7.3.17" : "Python 3.13.1") with { FullName = Guid.NewGuid().ToString("N") };
            var runner = new ImportChecker(info);
            manager.Register(runner, Token);
            runners.Add(runner);
            compilers.Add(await AddImportCompilerAsync(db, info));
        }
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { number = 1, name = "Languages", timeLimit = 4, slowTimeLimit = 9 }, true,
            ("A/solutions/main.cc", "C++ solution"u8.ToArray()), ("A/solutions/main.py", "Python solution"u8.ToArray())));
        await service.EnsureValidatorAsync(setup.Actor, preview, preview.Rows[0], Token);
        preview.Rows[0].Solutions[0].Selected = true;
        preview.Rows[0].Solutions[0].CompilerId = compilers[0].Id;
        await service.RunSolutionsAsync(setup.Actor, preview, preview.Rows[0], token: Token);
        Assert.NotEmpty(runners[0].Requests);
        Assert.Empty(runners[1].Requests);
        Assert.Empty(runners[2].Requests);
        preview.Rows[0].Solutions[0].Selected = false;
        preview.Rows[0].Solutions[1].Selected = true;
        preview.Rows[0].Solutions[1].CompilerId = compilers[1].Id;
        await service.RunSolutionsAsync(setup.Actor, preview, preview.Rows[0], token: Token);
        Assert.Empty(runners[2].Requests);
        preview.Rows[0].Solutions[1].CompilerId = compilers[2].Id;
        await service.RunSolutionsAsync(setup.Actor, preview, preview.Rows[0], token: Token);
        foreach (var runner in runners)
        {
            var request = Assert.Single(runner.Requests);
            Assert.Equal(TimeSpan.FromSeconds(request.Compilator.Configuration.UseSlowTimeLimit ? 9 : 4), request.TimeLimit);
            Assert.Equal(request.Compilator.Configuration.Language == "C++" ? "C++ solution" : "Python solution", request.Source);
        }
        Assert.Empty(preview.Rows[0].Errors);
        await service.SaveAsync(setup.Actor, preview, preview.Rows[0], Token);
    }

    [Fact]
    public async Task AvailableConfigurationsDeduplicateRunnersAndExcludeFailedSelfTestsAndVersions()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var manager = (CheckerManager)site.Services.GetRequiredService<ICheckerManager>();
        var info = new GccLinuxCpp23Configuration().ParseInfo("15.2.0") with { FullName = Guid.NewGuid().ToString("N") };
        var compiler = await AddImportCompilerAsync(scope.ServiceProvider.GetRequiredService<OlympContext>(), info);
        var first = new ImportChecker(info);
        var second = new ImportChecker(info);
        var failed = new ImportChecker(info with { FullName = Guid.NewGuid().ToString("N") }) { SelfTestingStatus = CheckerSelfTestingStatus.Failed };
        var broken = new ImportChecker(info with { FullName = Guid.NewGuid().ToString("N"), Error = "version failed" });
        foreach (var runner in new[] { first, second, failed, broken }) manager.Register(runner, Token);

        var configurations = ContestImportService.AvailableConfigurations(manager);
        Assert.Single(configurations, c => c.Info == info);
        Assert.DoesNotContain(configurations, c => c.Runner.Id == failed.Id || c.Runner.Id == broken.Id);

        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { number = 1, name = "Unregistered" }, true,
            ("A/validator.cpp", "validator"u8.ToArray()), ("A/solutions/main.cpp", "solution"u8.ToArray())));
        preview.ValidatorCompilerId = compiler.Id;
        await service.EnsureValidatorAsync(setup.Actor, preview, preview.Rows[0], Token);
        Assert.Equal(1, first.ValidatorCalls + second.ValidatorCalls);
        preview.Rows[0].Solutions[0].Selected = true;
        preview.Rows[0].Solutions[0].CompilerId = compiler.Id;
        var firstResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = 0;
        var run = service.RunSolutionsAsync(setup.Actor, preview, preview.Rows[0], async () =>
        {
            Assert.Equal(++notifications, preview.Rows[0].Runs.Count);
            if (notifications == 1)
            {
                firstResult.SetResult();
                await resume.Task.WaitAsync(Token);
            }
        }, token: Token);
        try
        {
            await firstResult.Task.WaitAsync(Token);
            Assert.Single(preview.Rows[0].Runs);
            Assert.False(run.IsCompleted);
        }
        finally
        {
            resume.TrySetResult();
        }
        await run;
        Assert.Equal(preview.Rows[0].Runs.Count, notifications);
        Assert.Single(first.Requests.Concat(second.Requests));
        Assert.Empty(failed.Requests);
        Assert.Empty(broken.Requests);
    }

    private sealed class ImportChecker(CompilatorInfo info) : ICheckerService
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Name => "Import test runner";
        public DateTimeOffset ConnectingTime => DateTimeOffset.UtcNow;
        public IReadOnlyList<ICompilatorConfiguration> Configurations => [info.Configuration];
        public IReadOnlyList<CompilatorInfo>? Compilators => [info];
        public CheckerSelfTestingStatus SelfTestingStatus { get; set; } = CheckerSelfTestingStatus.Success;
        public IEnumerable<CheckerTestResult> SelfTestingResults => [];
        public List<CheckRequest> Requests { get; } = [];
        public bool FailChecks { get; set; }
        public Func<CheckRequest, CancellationToken, Task<CheckerResult>>? OnCheck { get; set; }
        public int ValidatorCalls { get; private set; }
        public string? LastValidatorSource { get; private set; }
        public IReadOnlyList<ValidatorTest> LastValidatorTests { get; private set; } = [];
        public Func<IReadOnlyList<ValidatorTest>, ValidatorResult>? OnValidate { get; set; }
        public Task<bool> RunSelfTests(CancellationToken token) => Task.FromResult(false);
        public Task<CheckerResult> Run(string source, ICompilatorConfiguration configuration, string input, TimeSpan timeLimit, int memoryLimitMb, CancellationToken token) => throw new NotSupportedException();
        public Task<CheckerResult> Check(CheckRequest request, CancellationToken token)
        {
            Requests.Add(request);
            if (FailChecks) throw new IOException("Runner unavailable");
            if (OnCheck is not null) return OnCheck(request, token);
            return Task.FromResult(new CheckerResult(-1001, "informational failure", 0, MaxUserTime: TimeSpan.FromMilliseconds(5), MaxMemory: 1048576, SlowestTest: 1));
        }
        public Task<ValidatorResult> Validate(string source, CompilatorInfo compiler, IReadOnlyList<ValidatorTest> tests, CancellationToken token)
        {
            ValidatorCalls++;
            LastValidatorSource = source;
            LastValidatorTests = tests;
            if (OnValidate is not null) return Task.FromResult(OnValidate(tests));
            return Task.FromResult(new ValidatorResult(true, "validator log", [.. tests.Select(t => new ValidatorTestResult(t.Number,
                t.Number != 3))]));
        }
    }
}

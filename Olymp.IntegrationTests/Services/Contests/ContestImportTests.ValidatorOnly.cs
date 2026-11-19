using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Checker;
using Olymp.Domain;
using Olymp.WebApp;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Checker;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

public partial class ContestImportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdenticalValidatorCanReplaceAnInactiveCompilerWithoutRunningTheOldOne(bool validatorOnly)
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var (oldCompiler, oldRunner) = await AddReferenceCompilerAsync(db);
        byte[] source = "// unchanged validator"u8.ToArray();
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { number = 1, name = "Compiler replacement", inactiveTests = InactiveTestNumbers }, true,
            ("A/validator.cpp", source)));
        preview.ValidatorCompilerId = oldCompiler.Id;
        preview.Rows[0].Validator!.Save = true;
        await service.EnsureValidatorAsync(setup.Actor, preview, preview.Rows[0], Token);
        int id = await service.SaveAsync(setup.Actor, preview, preview.Rows[0], Token);
        int oldId = preview.Rows[0].SavedValidator!.Id;
        await db.Compilators.Where(compiler => compiler.Id == oldCompiler.Id).ExecuteUpdateAsync(set => set.SetProperty(compiler => compiler.IsActive, false), Token);
        var (newCompiler, newRunner) = await AddReferenceCompilerAsync(db);
        preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { id, inactiveTests = InactiveTestNumbers }, true, ("A/validator.cpp", source)));
        preview.ValidatorCompilerId = newCompiler.Id;
        var row = preview.Rows[0];
        row.Validator!.Save = true;
        if (validatorOnly)
        {
            var check = await service.CheckValidatorOnlyAsync(setup.Actor, preview, row, Token);
            await service.SaveValidatorOnlyAsync(setup.Actor, check, Token);
        }
        else
        {
            await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
            Assert.False(row.Validator.AlreadySaved);
            Assert.Equal([oldId], row.Validator.ReplacedIds);
            Assert.True(row.ReferenceValidatorChecked);
            Assert.True(row.HasChanges);
            await service.SaveAsync(setup.Actor, preview, row, Token);
        }
        var saved = await db.Submissions.AsNoTracking().SingleAsync(submission => submission.ProblemId == id && submission.IsReference, Token);
        Assert.Equal(newCompiler.Id, saved.CompilatorId);
        Assert.Equal(source, saved.SourceCode);
        Assert.False(await db.Submissions.Where(submission => submission.Id == oldId).Select(submission => submission.IsReference).SingleAsync(Token));
        Assert.Single(await db.CheckResults.Where(result => result.SubmissionId == oldId).ToListAsync(Token));
        Assert.Equal(1, oldRunner.ValidatorCalls);
        Assert.Equal(1, newRunner.ValidatorCalls);
    }

    [Fact]
    public async Task ValidatorOnlySaveRollsBackReplacementIfContestStartsDuringSaving()
    {
        var setup = await SetupAsync();
        using var initialScope = site.Services.CreateScope();
        var initialDb = initialScope.ServiceProvider.GetRequiredService<OlympContext>();
        var initialService = initialScope.ServiceProvider.GetRequiredService<ContestImportService>();
        var (compiler, runner) = await AddReferenceCompilerAsync(initialDb);
        var preview = await PrepareAsync(initialService, setup.Actor, setup.Contest.Id, Package(new { number = 1, name = "Boundary", inactiveTests = InactiveTestNumbers }, true,
            ("A/validator.cpp", "// old validator"u8.ToArray())));
        preview.ValidatorCompilerId = compiler.Id;
        preview.Rows[0].Validator!.Save = true;
        await initialService.EnsureValidatorAsync(setup.Actor, preview, preview.Rows[0], Token);
        int id = await initialService.SaveAsync(setup.Actor, preview, preview.Rows[0], Token);
        int oldId = preview.Rows[0].SavedValidator!.Id;
        var jury = await site.AddUserAsync(RoleNames.Jury);
        var clock = new CrossingContestStartClock(setup.Contest.StartTime);
        using var host = site.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var scope = host.Services.CreateScope();
        ((CheckerManager)host.Services.GetRequiredService<ICheckerManager>()).Register(runner, Token);
        await scope.ServiceProvider.GetRequiredService<ContestImportAccessService>().SetAsync(setup.Actor, setup.Contest.Id, jury.Id, true, Token);
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        preview = await PrepareAsync(service, Actor(jury), setup.Contest.Id, Package(new { id }, true, ("A/validator.cpp", "// new validator"u8.ToArray())));
        preview.ValidatorCompilerId = compiler.Id;
        var check = await service.CheckValidatorOnlyAsync(Actor(jury), preview, preview.Rows[0], Token);
        clock.CrossStart = true;
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveValidatorOnlyAsync(Actor(jury), check, Token));
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.True(await db.Submissions.Where(submission => submission.Id == oldId).Select(submission => submission.IsReference).SingleAsync(Token));
        Assert.Single(await db.Submissions.Where(submission => submission.ProblemId == id).ToListAsync(Token));
    }

    private sealed class CrossingContestStartClock(DateTimeOffset start) : TimeProvider
    {
        public bool CrossStart { get; set; }
        private int _reads;
        public override DateTimeOffset GetUtcNow() => CrossStart && ++_reads > 1 ? start : start.AddTicks(-1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidatorOnlySaveUsesStoredTestsAndPreservesProblemOtherSolutionsAndOldHistory(bool replace)
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var (compiler, runner) = await AddReferenceCompilerAsync(db);
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { number = 1, name = "Stored", inactiveTests = InactiveTestNumbers }, true,
            [("A/solutions/main.cpp", "// stored reference"u8.ToArray()),
            .. replace ? new (string, byte[])[] { ("A/validator.cpp", "// original validator"u8.ToArray()) } : []]));
        preview.ValidatorCompilerId = compiler.Id;
        var row = preview.Rows[0];
        row.Solutions[0].Save = true;
        row.Solutions[0].CompilerId = compiler.Id;
        if (row.Validator is { } validator) validator.Save = true;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        int id = await service.SaveAsync(setup.Actor, preview, row, Token);
        int? oldValidatorId = row.SavedValidator?.Id;
        int solutionId = Assert.Single(row.ReferenceSolutions).Id;
        int historyCount = await db.CheckResults.CountAsync(result => result.SubmissionId == solutionId, Token);
        await db.Tests.Where(test => test.ProblemId == id).ExecuteUpdateAsync(set => set.SetProperty(test => test.IsActive, true), Token);
        await db.Tests.Where(test => test.ProblemId == id && test.Number == 1)
            .ExecuteUpdateAsync(set => set.SetProperty(test => test.Input, [97, 13, 98]), Token);
        var stored = await db.Tests.AsNoTracking().Where(test => test.ProblemId == id).OrderBy(test => test.Number).ToArrayAsync(Token);
        preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { id, name = "Archive change", timeLimit = 9 }, true,
            ("A/validator.cpp", "// replacement validator"u8.ToArray()), ("A/solutions/new.cpp", "// new reference"u8.ToArray())));
        preview.ValidatorCompilerId = compiler.Id;
        row = preview.Rows[0];
        row.Validator!.Save = true;
        row.Solutions[0].Save = true;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        Assert.NotEmpty(row.ValidatorErrors);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(setup.Actor, preview, row, Token));
        runner.Requests.Clear();
        var check = await service.CheckValidatorOnlyAsync(setup.Actor, preview, row, Token);
        Assert.Equal([3], check.RejectedTests);
        Assert.Equal(stored[0].Input, runner.LastValidatorTests[0].Input.ToArray());
        await service.SaveValidatorOnlyAsync(setup.Actor, check, Token);
        Assert.Empty(runner.Requests);
        Assert.Equal("Stored", await db.Problems.Where(problem => problem.Id == id).Select(problem => problem.Name).SingleAsync(Token));
        Assert.Equal(1, await db.Problems.Where(problem => problem.Id == id).Select(problem => problem.TimeLimit).SingleAsync(Token));
        var after = await db.Tests.AsNoTracking().Where(test => test.ProblemId == id).OrderBy(test => test.Number).ToArrayAsync(Token);
        Assert.Equal(stored.Select(test => test.Id), after.Select(test => test.Id));
        Assert.Equal(stored[0].Input, after[0].Input);
        Assert.Equal(historyCount, await db.CheckResults.CountAsync(result => result.SubmissionId == solutionId, Token));
        Assert.False(await db.Submissions.AnyAsync(submission => submission.ProblemId == id && submission.Description == "new.cpp", Token));
        var saved = await db.Submissions.AsNoTracking().SingleAsync(submission => submission.ProblemId == id && submission.IsReference && submission.Description == "validator.cpp", Token);
        Assert.Equal("// replacement validator", saved.Text);
        Assert.True(saved.IsHidden);
        Assert.Equal(-1003, saved.StatusCode);
        Assert.Single(await db.CheckResults.Where(result => result.SubmissionId == saved.Id).ToListAsync(Token));
        if (oldValidatorId is { } oldId)
        {
            Assert.False(await db.Submissions.Where(submission => submission.Id == oldId).Select(submission => submission.IsReference).SingleAsync(Token));
            Assert.Single(await db.CheckResults.Where(result => result.SubmissionId == oldId).ToListAsync(Token));
        }
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveValidatorOnlyAsync(setup.Actor, check, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidatorOnlyRejectsCompilationFailureAndIncompleteResults(bool compiled)
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var (compiler, runner) = await AddReferenceCompilerAsync(db);
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { number = 1, name = "Stored" }));
        await service.EnsureValidatorAsync(setup.Actor, preview, preview.Rows[0], Token);
        int id = await service.SaveAsync(setup.Actor, preview, preview.Rows[0], Token);
        preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { id }, true, ("A/validator.cpp", "// validator"u8.ToArray())));
        preview.ValidatorCompilerId = compiler.Id;
        runner.OnValidate = _ => new ValidatorResult(compiled, "failed or incomplete", []);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.CheckValidatorOnlyAsync(setup.Actor, preview, preview.Rows[0], Token));
        Assert.False(await db.Submissions.AnyAsync(submission => submission.ProblemId == id, Token));
    }

    [Theory]
    [InlineData("tests")]
    [InlineData("permission")]
    [InlineData("compiler")]
    public async Task ValidatorOnlySaveRechecksStoredTestsPermissionAndCompiler(string change)
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var (compiler, runner) = await AddReferenceCompilerAsync(db);
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { number = 1, name = "Stored" }));
        await service.EnsureValidatorAsync(setup.Actor, preview, preview.Rows[0], Token);
        int id = await service.SaveAsync(setup.Actor, preview, preview.Rows[0], Token);
        preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { id }, true, ("A/validator.cpp", "// validator"u8.ToArray())));
        preview.ValidatorCompilerId = compiler.Id;
        var check = await service.CheckValidatorOnlyAsync(setup.Actor, preview, preview.Rows[0], Token);
        switch (change)
        {
            case "tests":
                await db.Tests.Where(test => test.ProblemId == id).ExecuteUpdateAsync(set => set.SetProperty(test => test.Input, [99]), Token);
                break;
            case "permission":
                await db.UserClaims.Where(claim => claim.ClaimType == ClaimNames.ContestManage && claim.ClaimValue == setup.Contest.Id.ToString()).ExecuteDeleteAsync(Token);
                break;
            case "compiler":
                await db.Compilators.Where(item => item.Id == compiler.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false), Token);
                break;
        }
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveValidatorOnlyAsync(setup.Actor, check, Token));
        Assert.False(await db.Submissions.AnyAsync(submission => submission.ProblemId == id, Token));
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Checker;
using Olymp.Checker.Compilators;
using Olymp.Domain;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

public partial class ContestImportTests
{
    [Fact]
    public async Task UnchangedImportUsesSavedValidatorAndAcceptedReferenceWithoutRuns()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var (compiler, runner) = await AddReferenceCompilerAsync(db);
        runner.OnValidate = tests => new(true, "valid", [.. tests.Select(test => new ValidatorTestResult(test.Number, true))]);
        runner.OnCheck = (_, _) => Task.FromResult(new CheckerResult(2, "accepted"));
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { number = 1, name = "Original" }, true,
            ("A/validator.cpp", "// validator"u8.ToArray()), ("A/solutions/main.cpp", "// reference"u8.ToArray())));
        preview.ValidatorCompilerId = compiler.Id;
        var row = preview.Rows[0];
        row.Validator!.Save = true;
        row.Solutions[0].Save = true;
        row.Solutions[0].CompilerId = compiler.Id;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        int id = await service.SaveAsync(setup.Actor, preview, row, Token);
        int runs = runner.Requests.Count, validations = runner.ValidatorCalls;
        preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { id, name = "Renamed" }, false));
        row = preview.Rows[0];
        Assert.True(row.ValidatorPassed);
        Assert.True(row.HasAcceptedReference);
        await service.PrepareValidationAsync(setup.Actor, preview, row, Token);
        await service.SaveAsync(setup.Actor, preview, row, Token);
        Assert.Equal(runs, runner.Requests.Count);
        Assert.Equal(validations, runner.ValidatorCalls);
        row.TimeLimit++;
        Assert.False(row.HasAcceptedReference);
        Assert.True(row.ValidatorPassed);
        preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { id }, true,
            ("A/validator.cpp", "// changed validator"u8.ToArray())));
        row = preview.Rows[0];
        Assert.Null(row.ValidatorPassed);
        var validator = await db.Submissions.AsTracking().SingleAsync(submission => submission.ProblemId == id && submission.Description == "validator.cpp", Token);
        db.CheckResults.Add(new() { Submission = validator, CheckTime = DateTimeOffset.UtcNow.AddSeconds(1), StatusCode = -1001, Log = "invalid" });
        await db.SaveChangesAsync(Token);
        preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { id }, false));
        Assert.False(preview.Rows[0].ValidatorPassed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MeasurementUsesFiveTimesMatchingLimitAndDoesNotSaveResults(bool slow)
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var (compiler, runner) = await AddReferenceCompilerAsync(db, slow ? new CPythonConfiguration() : new GccLinuxCpp23Configuration());
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id,
            Package(new { number = 1, name = "Measure", timeLimit = 2, slowTimeLimit = 7, memoryLimit = 128 }, true,
                ($"A/solutions/first{compiler.SourceExtension}", "// first reference source"u8.ToArray()),
                ($"A/solutions/second{compiler.SourceExtension}", "// second reference source"u8.ToArray())));
        var row = preview.Rows[0];
        foreach (var solution in row.Solutions) { solution.Save = true; solution.CompilerId = compiler.Id; }
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        int id = await service.SaveAsync(setup.Actor, preview, row, Token);
        int history = await db.CheckResults.CountAsync(check => check.Submission.ProblemId == id, Token);
        runner.Requests.Clear();
        var results = new List<LimitMeasurement>();
        await scope.ServiceProvider.GetRequiredService<ContestProblemService>().MeasureLimitsAsync(setup.Actor, id,
            result => { results.Add(result); return Task.CompletedTask; }, Token);
        Assert.Equal(2, results.Count);
        Assert.All(runner.Requests, request =>
        {
            Assert.False(request.AllTests);
            Assert.Equal(TimeSpan.FromSeconds((slow ? 7 : 2) * 5), request.TimeLimit);
            Assert.Equal(128, request.MemoryLimitMb);
            Assert.Equal([1, 3], request.Tests.Select(test => test.Number));
        });
        Assert.All(results, result => Assert.Equal(slow ? 7 : 2, result.TimeLimit));
        Assert.Equal(history, await db.CheckResults.CountAsync(check => check.Submission.ProblemId == id, Token));
        Assert.Equal(2, await db.Problems.Where(problem => problem.Id == id).Select(problem => problem.TimeLimit).SingleAsync(Token));
    }
}

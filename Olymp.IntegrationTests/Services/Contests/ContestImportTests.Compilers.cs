using Microsoft.Extensions.DependencyInjection;

using Olymp.Checker;
using Olymp.Domain;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Checker;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

public partial class ContestImportTests
{
    [Fact]
    public async Task UnavailableDatabaseCompilerKeepsSelectionReportsErrorsAndRetriesWhenRunnerReturns()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var (compiler, runner) = await AddReferenceCompilerAsync(db);
        var (_, otherRunner) = await AddReferenceCompilerAsync(db);
        runner.SelfTestingStatus = CheckerSelfTestingStatus.Failed;
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { number = 1, name = "Offline compiler", inactiveTests = InactiveTestNumbers }, true,
            ("A/validator.cpp", "// validator"u8.ToArray()), ("A/solutions/main.cpp", "// solution"u8.ToArray())));
        preview.ValidatorCompilerId = compiler.Id;
        var row = preview.Rows[0];
        row.Solutions[0].CompilerId = compiler.Id;
        row.Solutions[0].Selected = true;
        Assert.Contains(row.Solutions[0].Compilers, item => item.Id == compiler.Id);
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        Assert.Contains(SiteResources.ImportNoValidatorRunner, row.ValidatorErrors);
        Assert.Null(row.ValidatorResult);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        Assert.Equal(SiteResources.ImportCompilerRunnerUnavailable, Assert.Single(row.Runs).Message);
        Assert.Equal(compiler.Id, preview.ValidatorCompilerId);
        Assert.Equal(compiler.Id, row.Validator!.CompilerId);
        Assert.Equal(compiler.Id, row.Solutions[0].CompilerId);
        Assert.Empty(otherRunner.Requests);
        Assert.Equal(0, otherRunner.ValidatorCalls);
        runner.SelfTestingStatus = CheckerSelfTestingStatus.Success;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        Assert.Empty(row.ValidatorErrors);
        Assert.NotNull(row.ValidatorResult);
        Assert.NotNull(Assert.Single(row.Runs).Result);
        Assert.Single(runner.Requests);
        runner.SelfTestingStatus = CheckerSelfTestingStatus.Failed;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        Assert.Contains(SiteResources.ImportNoValidatorRunner, row.ValidatorErrors);
        Assert.Null(row.ValidatorResult);
    }
}

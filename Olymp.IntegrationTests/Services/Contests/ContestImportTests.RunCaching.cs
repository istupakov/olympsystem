using Microsoft.Extensions.DependencyInjection;

using Olymp.Checker;
using Olymp.Checker.Compilators;
using Olymp.Domain;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

public partial class ContestImportTests
{
    [Fact]
    public async Task UnrelatedCompilerAndRunnerChangesKeepCompletedChecksReusable()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var (compiler, runner) = await AddReferenceCompilerAsync(db);
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { number = 1, name = "Stable cache", inactiveTests = InactiveTestNumbers }, true,
            ("A/validator.cpp", "// validator"u8.ToArray()), ("A/solutions/main.cpp", "// solution"u8.ToArray())));
        preview.ValidatorCompilerId = compiler.Id;
        var row = preview.Rows[0];
        row.Validator!.Save = true;
        row.Solutions[0].Save = true;
        row.Solutions[0].CompilerId = compiler.Id;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        var (_, unrelated) = await AddReferenceCompilerAsync(db, new CPythonConfiguration());
        foreach (var status in new[] { CheckerSelfTestingStatus.Success, CheckerSelfTestingStatus.Failed, CheckerSelfTestingStatus.Success })
        {
            unrelated.SelfTestingStatus = status;
            await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
            Assert.True(row.ReferencesChecked);
            await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
            Assert.Single(runner.Requests);
            Assert.Equal(1, runner.ValidatorCalls);
        }
        await service.SaveAsync(setup.Actor, preview, row, Token);
        Assert.True(row.IsSaved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunnerFailuresAreRetriedInsteadOfReused(bool internalError)
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var (compiler, runner) = await AddReferenceCompilerAsync(db);
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { number = 1, name = "Retry" }, true,
            ("A/solutions/main.cpp", "// source code"u8.ToArray())));
        var row = Assert.Single(preview.Rows);
        row.Solutions[0].Save = true;
        row.Solutions[0].CompilerId = compiler.Id;
        if (internalError) runner.OnCheck = (_, _) => Task.FromResult(new CheckerResult(-10, "internal failure"));
        else runner.FailChecks = true;
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        Assert.False(row.ReferencesChecked);
        runner.OnCheck = null;
        runner.FailChecks = false;
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        Assert.Equal(2, runner.Requests.Count);
        Assert.True(row.ReferencesChecked);
    }

    [Fact]
    public async Task SavedReferencesReuseResultsAfterRefreshAndOnlyChangedLimitIsRerun()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var (regularCompiler, regular) = await AddReferenceCompilerAsync(db);
        var (slowCompiler, slow) = await AddReferenceCompilerAsync(db, new CPythonConfiguration());
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(
            new { number = 1, name = "Cached checks", inactiveTests = InactiveTestNumbers }, true,
            ("A/validator.cpp", "validator"u8.ToArray()),
            ("A/solutions/main.cpp", "regular solution"u8.ToArray()),
            ("A/solutions/main.py", "slow solution"u8.ToArray())));
        preview.ValidatorCompilerId = regularCompiler.Id;
        var row = Assert.Single(preview.Rows);
        foreach (var file in row.Solutions)
        {
            file.Save = true;
            file.Selected = true;
            file.CompilerId = file.Filename.EndsWith(".cpp", StringComparison.Ordinal) ? regularCompiler.Id : slowCompiler.Id;
        }
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        Assert.True(row.ReferencesChecked);
        await service.SaveAsync(setup.Actor, preview, row, Token);
        await service.RefreshAsync(setup.Actor, preview, row, Token);
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        Assert.Single(regular.Requests);
        Assert.Single(slow.Requests);
        Assert.Equal(1, regular.ValidatorCalls);
        row.TimeLimit++;
        Assert.False(row.ReferencesChecked);
        var error = await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(setup.Actor, preview, row, Token));
        Assert.Equal(SiteResources.ImportReferenceChecksRequired, error.Message);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        Assert.Equal(2, regular.Requests.Count);
        Assert.Single(slow.Requests);
        Assert.True(row.ReferencesChecked);
        row.SlowTimeLimit++;
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        Assert.Equal(2, regular.Requests.Count);
        Assert.Equal(2, slow.Requests.Count);
        preview.LineEndings = ImportLineEndings.Lf;
        Assert.False(row.ValidatorChecked);
        Assert.False(row.ReferencesChecked);
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        Assert.Equal(3, regular.Requests.Count);
        Assert.Equal(3, slow.Requests.Count);
        Assert.Equal(2, regular.ValidatorCalls);
    }

    [Fact]
    public async Task LimitChangedDuringRunKeepsActualLimitAndDoesNotAuthorizeSaving()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var (compiler, runner) = await AddReferenceCompilerAsync(db);
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { number = 1, name = "In flight" }, true,
            ("A/solutions/main.cpp", "// source code"u8.ToArray())));
        var row = Assert.Single(preview.Rows);
        row.Solutions[0].Save = true;
        row.Solutions[0].CompilerId = compiler.Id;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<CheckerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.OnCheck = (_, token) =>
        {
            started.TrySetResult();
            return resume.Task.WaitAsync(token);
        };
        var checking = service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        try
        {
            await started.Task.WaitAsync(Token);
            row.TimeLimit = 7;
        }
        finally { resume.TrySetResult(new(2, "completed")); }
        await checking;
        Assert.Equal(1, Assert.Single(row.Runs).TimeLimit);
        Assert.False(row.IsCurrent(row.Runs[0]));
        Assert.False(row.ReferencesChecked);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(setup.Actor, preview, row, Token));
        runner.OnCheck = null;
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        Assert.Equal(7, Assert.Single(row.Runs).TimeLimit);
        Assert.True(row.ReferencesChecked);
    }

    [Fact]
    public async Task CancelledBatchPreservesCompletedResultsAndForceRerunsThem()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var (compiler, runner) = await AddReferenceCompilerAsync(db);
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { number = 1, name = "Resume" }, true,
            ("A/solutions/first.cpp", "first solution"u8.ToArray()), ("A/solutions/second.cpp", "second solution"u8.ToArray())));
        var row = Assert.Single(preview.Rows);
        foreach (var file in row.Solutions) { file.Save = true; file.CompilerId = compiler.Id; }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        runner.OnCheck = async (_, token) =>
        {
            if (runner.Requests.Count == 2)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return new(2, "completed");
        };
        var checking = service.RunSolutionsAsync(setup.Actor, preview, row, token: cancel.Token);
        try { await started.Task.WaitAsync(Token); }
        finally { cancel.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checking);
        Assert.Single(row.Runs);
        runner.OnCheck = null;
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        Assert.Equal(3, runner.Requests.Count);
        Assert.True(row.ReferencesChecked);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token, force: true);
        Assert.Equal(5, runner.Requests.Count);
        Assert.Equal(2, row.RunsCompleted);
        Assert.Equal(2, row.RunsTotal);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.WebApp.Services.Checker;

using Xunit;

using static Olymp.IntegrationTests.Services.Checkers.CheckerTestData;

namespace Olymp.IntegrationTests.Services.Checkers;

public partial class CompilatorSynchronizationTests
{
    [Fact]
    public async Task ChangedCommandCreatesDisabledRecordAndPreservesOldConfiguration()
    {
        var configuration = Configuration();
        await using var site = WithRunnerConfiguration(factory, configuration);
        var manager = site.Services.GetRequiredService<ICheckerManager>();
        var runner = new TestRunner();
        await using var connection = await RunnerRegistration.ConnectAsync(manager, runner);
        var token = TestContext.Current.CancellationToken;
        int oldId = await manager.AddCompilatorAsync(runner.Id, configuration.Name, configuration.DisplayName, token: token);
        await manager.SetCompilatorActiveAsync(oldId, true, token);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Compilators.Where(item => item.Id == oldId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CommandLine, "compile old"), token);
        int newId = await manager.AddCompilatorAsync(runner.Id, configuration.Name, configuration.DisplayName, token: token);
        Assert.NotEqual(oldId, newId);
        var old = await db.Compilators.AsNoTracking().SingleAsync(item => item.Id == oldId, token);
        var current = await db.Compilators.AsNoTracking().SingleAsync(item => item.Id == newId, token);
        Assert.Equal("compile old", old.CommandLine);
        Assert.True(old.IsActive);
        Assert.False(current.IsActive);
        Assert.False(connection.Checker.CanCheck(old));
        Assert.True(connection.Checker.CanCheck(current));
        var stalePreview = CompilerConfigurationKey.From(old);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.AddCompilatorAsync(runner.Id,
            configuration.Name, configuration.DisplayName, stalePreview, token));
    }

    [Fact]
    public async Task SeparateAdditionStartsDisabledAndDoesNotChangeExistingAvailability()
    {
        var configuration = Configuration();
        await using var site = WithRunnerConfiguration(factory, configuration);
        var manager = site.Services.GetRequiredService<ICheckerManager>();
        var runner = new TestRunner();
        await using var connection = await RunnerRegistration.ConnectAsync(manager, runner);
        var token = TestContext.Current.CancellationToken;
        int id = await manager.AddCompilatorAsync(runner.Id, configuration.Name, configuration.DisplayName, token: token);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False(await db.Compilators.Where(item => item.Id == id).Select(item => item.IsActive).SingleAsync(token));
        await manager.SetCompilatorActiveAsync(id, true, token);
        Assert.True(await db.Compilators.Where(item => item.Id == id).Select(item => item.IsActive).SingleAsync(token));
        Assert.Equal(id, await manager.AddCompilatorAsync(runner.Id, configuration.Name, configuration.DisplayName, token: token));
        Assert.True(await db.Compilators.Where(item => item.Id == id).Select(item => item.IsActive).SingleAsync(token));
        await manager.SetCompilatorActiveAsync(id, false, token);
        Assert.False(await db.Compilators.Where(item => item.Id == id).Select(item => item.IsActive).SingleAsync(token));
        Assert.Single(await db.Compilators.Where(item => item.Name == configuration.Name).ToListAsync(token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparateAdditionRequiresConnectedRunnerWithSuccessfulSelfTests(bool connected)
    {
        var configuration = Configuration();
        await using var site = WithRunnerConfiguration(factory, configuration);
        var manager = site.Services.GetRequiredService<ICheckerManager>();
        var runner = new TestRunner { VersionStatus = Olymp.Checker.RunnerStatus.UserTimeLimit };
        await using var connection = connected ? await RunnerRegistration.ConnectAsync(manager, runner) : null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.AddCompilatorAsync(runner.Id,
            configuration.Name, configuration.DisplayName, token: TestContext.Current.CancellationToken));
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OlympContext>().Compilators
            .AnyAsync(item => item.Name == configuration.Name, TestContext.Current.CancellationToken));
    }
}

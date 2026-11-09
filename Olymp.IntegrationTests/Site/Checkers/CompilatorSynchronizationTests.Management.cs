using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Site.Services.Checker;

using Xunit;

using static Olymp.IntegrationTests.Site.Checkers.CheckerTestData;

namespace Olymp.IntegrationTests.Site.Checkers;

public partial class CompilatorSynchronizationTests
{
    [Fact]
    public async Task SeparateAdditionStartsDisabledAndDoesNotChangeExistingAvailability()
    {
        var configuration = Configuration();
        await using var site = WithRunnerConfiguration(factory, configuration);
        var manager = site.Services.GetRequiredService<ICheckerManager>();
        var runner = new TestRunner();
        await using var connection = await RunnerRegistration.ConnectAsync(manager, runner);
        var token = TestContext.Current.CancellationToken;
        int id = await manager.AddCompilatorAsync(runner.Id, configuration.Name, configuration.DisplayName, token);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False(await db.Compilators.Where(item => item.Id == id).Select(item => item.IsActive).SingleAsync(token));
        await manager.SetCompilatorActiveAsync(id, true, token);
        Assert.True(await db.Compilators.Where(item => item.Id == id).Select(item => item.IsActive).SingleAsync(token));
        Assert.Equal(id, await manager.AddCompilatorAsync(runner.Id, configuration.Name, configuration.DisplayName, token));
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
            configuration.Name, configuration.DisplayName, TestContext.Current.CancellationToken));
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OlympContext>().Compilators
            .AnyAsync(item => item.Name == configuration.Name, TestContext.Current.CancellationToken));
    }
}

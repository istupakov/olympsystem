using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Checker;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.WebApp.Services.Checker;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

[Trait("Category", "Integration")]
public class FakeRunnerTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("Development", true, 2)]
    [InlineData("Development", false, 0)]
    [InlineData("Production", true, 0)]
    public void FakeRunnersUseNormalManagerOnlyWhenEnabledInDevelopment(string environment, bool enabled, int count)
    {
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Checker:FakeRunners"] = enabled.ToString() }));
        });
        var manager = host.Services.GetRequiredService<ICheckerManager>();
        Assert.Equal(count, manager.Checkers.Count);
        if (count > 0)
        {
            Assert.All(manager.Checkers.Values, checker => Assert.IsType<FakeCheckerService>(checker));
            Assert.Single(manager.Checkers.Values, checker => checker.SelfTestingStatus == CheckerSelfTestingStatus.Success);
        }
    }

    [Fact]
    public async Task FullFakeRunReturnsMarkedResultsAndContinuesAfterError()
    {
        var checker = FakeCheckerService.Create()[0];
        var request = new CheckRequest("test", "source", checker.Compilators![0], 1,
            [new(1, default, default, 20), new(2, default, default, 30), new(3, default, default, 50)], TimeSpan.FromSeconds(3), 256, true);
        var full = await checker.Check(request, TestContext.Current.CancellationToken);
        Assert.Equal(-1002, full.StatusCode); Assert.Equal(70, full.Score);
        Assert.Equal("20;WrongAnswer;50", full.TestResults);
        Assert.Contains("TEST RESULT", full.Log); Assert.Contains("Test 3", full.Log);
        var firstError = await checker.Check(request with { AllTests = false }, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Test 3", firstError.Log);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.Check(request, stop.Token));
    }
}

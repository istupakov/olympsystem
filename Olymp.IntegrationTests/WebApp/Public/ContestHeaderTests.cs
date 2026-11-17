using System.Reflection;
using System.Security.Claims;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.IntegrationTests.Shared;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Contests;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Public;

[Trait("Category", "Integration")]
public class ContestHeaderTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Fact]
    public async Task OverlappingUpdatesKeepASingleOfficialStandingParameter()
    {
        var token = TestContext.Current.CancellationToken;
        var contest = TestEntities.CreateContest("Header updates", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1));
        var participant = TestEntities.CreateCompetitor(contest, "Header participant");
        using (var setup = factory.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<OlympContext>();
            db.Competitors.Add(participant);
            await db.SaveChangesAsync(token);
        }
        var loading = new AsyncQueryInterceptor("SELECT CASE");
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<OlympContext>(options => options.AddInterceptors(loading))));
        using var scope = host.Services.CreateScope();
        var header = new ContestHeader();
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new(ClaimTypes.NameIdentifier, participant.Id.ToString()), new(ClaimNames.CompetitorContest, contest.Id.ToString())], "Test"));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ContestHeader).GetProperty(nameof(ContestHeader.Contest))!.SetValue(header, contest);
        typeof(ContestHeader).GetProperty("ContextFactory", flags)!.SetValue(header, scope.ServiceProvider.GetRequiredService<IDbContextFactory<OlympContext>>());
        typeof(ContestHeader).GetProperty("AuthenticationState", flags)!.SetValue(header, Task.FromResult(new AuthenticationState(user)));
        var update = typeof(ContestHeader).GetMethod("OnParametersSetAsync", flags)!;
        await Dispatcher.CreateDefault().InvokeAsync(() =>
            Task.WhenAll((Task)update.Invoke(header, null)!, (Task)update.Invoke(header, null)!));
        Assert.True(loading.WasDelayed);
        Assert.Equal($"contests/{contest.Id}/standing?highlight=true&official=true",
            typeof(ContestHeader).GetField("standingUrl", flags)!.GetValue(header));
    }
}

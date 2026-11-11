using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

public partial class PracticeSubmissionTests
{
    [Theory]
    [InlineData("192.0.2.10", "203.0.113.42", "203.0.113.42", false)]
    [InlineData("192.0.2.10", "2001:db8::42", "2001:db8::42", false)]
    [InlineData("192.0.2.10", "198.51.100.99, 203.0.113.42", "203.0.113.42", false)]
    [InlineData("192.0.2.11", "203.0.113.42", "192.0.2.11", false)]
    [InlineData("203.0.113.42", null, "203.0.113.42", false)]
    [InlineData("192.0.2.10", "203.0.113.42", "203.0.113.42", true)]
    public async Task PublicSubmissionStoresOnlyServerResolvedIp(string peer, string? forwarded, string expected, bool temporary)
    {
        var (user, contest, problem, compiler) = await SeedAsync();
        User author = user;
        if (temporary)
        {
            await using var setup = factory.Services.CreateAsyncScope();
            var db = setup.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Contests.Where(item => item.Id == contest.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.StartTime, DateTimeOffset.UtcNow.AddHours(-1))
                .SetProperty(item => item.EndTime, DateTimeOffset.UtcNow.AddHours(1)), TestContext.Current.CancellationToken);
            var participant = Olymp.Tests.Shared.TestEntities.CreateCompetitor(contest, $"ip_{Guid.NewGuid():N}");
            participant.Contest = null!;
            participant.UserName = participant.Name;
            var users = setup.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.True((await users.CreateAsync(participant, WebAppFactory.Password)).Succeeded);
            Assert.True((await users.AddClaimAsync(participant,
                new(ClaimNames.CompetitorContest, contest.Id.ToString()))).Succeeded);
            author = participant;
        }
        await using var proxied = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ReverseProxy:KnownProxies:0"] = "192.0.2.10" }));
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new PeerAddressFilter(peer)));
        });
        using var client = proxied.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        if (forwarded is not null)
        {
            client.DefaultRequestHeaders.Add("X-Forwarded-For", forwarded);
            client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        }
        using var login = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new()
        {
            ["Input.Login"] = author.UserName!,
            ["Input.Password"] = WebAppFactory.Password
        });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        string path = ProblemPath(contest.Id, problem.Id);
        using var page = await GetAsync(client, path);
        var fields = Fields(page, compiler.Id, Source);
        fields["Input.CommiterInfo"] = "198.51.100.250";
        using var content = new FormUrlEncodedContent(fields);
        using var response = await client.PostAsync(path, content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<OlympContext>().Submissions
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected, saved.CommiterInfo);
        Assert.Equal(author.Id, saved.UserId);
    }

    private sealed class PeerAddressFilter(string address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextRequest) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(address);
                return nextRequest(context);
            });
            next(app);
        };
    }
}

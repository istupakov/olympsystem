using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.WebApp.Services;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Infrastructure;

[Trait("Category", "Integration")]
public class ClientConnectionTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("192.0.2.10", "203.0.113.42", "203.0.113.42")]
    [InlineData("192.0.2.10", "2001:db8::42", "2001:db8::42")]
    [InlineData("192.0.2.11", "203.0.113.42", "192.0.2.11")]
    [InlineData("203.0.113.42", null, "203.0.113.42")]
    public async Task CircuitCapturesServerResolvedAddressAndKeepsItAfterRequest(string peer, string? forwarded, string expected)
    {
        string? captured = null;
        await using var site = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ReverseProxy:KnownProxies:0"] = "192.0.2.10" }));
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new CaptureAddressFilter(peer, address => captured = address)));
        });
        using var client = site.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        if (forwarded is not null) client.DefaultRequestHeaders.Add("X-Forwarded-For", forwarded);
        using var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        Assert.Equal(expected, captured);
    }

    private sealed class CaptureAddressFilter(string peer, Action<string?> capture) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextRequest) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                await nextRequest(context);
                var connection = context.RequestServices.GetRequiredService<ClientConnection>();
                Assert.Same(connection, Assert.Single(context.RequestServices.GetServices<CircuitHandler>().OfType<ClientConnection>()));
                await connection.OnConnectionUpAsync(null!, context.RequestAborted);
                var http = context.RequestServices.GetRequiredService<IHttpContextAccessor>();
                http.HttpContext = null;
                try
                {
                    await connection.OnConnectionUpAsync(null!, context.RequestAborted);
                    capture(connection.RemoteIpAddress);
                }
                finally { http.HttpContext = context; }
            });
            next(app);
        };
    }
}

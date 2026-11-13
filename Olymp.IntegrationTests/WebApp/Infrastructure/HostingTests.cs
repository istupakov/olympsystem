using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Olymp.IntegrationTests.WebApp.Shared;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Infrastructure;

public class HostingTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("192.0.2.10", "https")]
    [InlineData("192.0.2.11", "http")]
    public async Task OriginalSchemeIsAcceptedOnlyFromKnownProxy(string peer, string expected)
    {
        await using var proxied = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ReverseProxy:KnownProxies:0"] = "192.0.2.10" }));
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new SchemeFilter(peer)));
        });
        using var client = proxied.CreateClient(new() { BaseAddress = new("http://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var response = await client.GetAsync("/test/scheme", TestContext.Current.CancellationToken);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DevelopmentHealthCheckRedirectsToHttps()
    {
        await using var development = factory.WithWebHostBuilder(builder => builder.UseSetting("https_port", "5001"));
        using var client = development.CreateClient(new() { BaseAddress = new("http://localhost"), AllowAutoRedirect = false });
        using var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(new Uri("https://localhost:5001/healthz"), response.Headers.Location);
    }

    [Theory]
    [InlineData("192.0.2.10", "https", "ru")]
    [InlineData("192.0.2.10", "https", "en")]
    [InlineData("192.0.2.11", "http", "ru")]
    public async Task LoginRedirectUsesForwardedSchemeAndSelectedLanguage(string peer, string expectedScheme, string culture)
    {
        await using var proxied = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ReverseProxy:KnownProxies:0"] = "192.0.2.10" }));
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new SchemeFilter(peer)));
        });
        using var client = proxied.CreateClient(new() { BaseAddress = new("http://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/test/auth", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expectedScheme, response.Headers.Location!.Scheme);
        Assert.Equal("/Account/Login", response.Headers.Location.AbsolutePath);
        Assert.Equal([culture], response.Content.Headers.ContentLanguage);
    }

    [Fact]
    public async Task ProductionHealthCheckAndKeysSurviveApplicationReplacement()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OlympKeys_" + Guid.NewGuid().ToString("N"));
        string? protectedText = null;
        try
        {
            for (int start = 0; start < 2; start++)
            {
                await using var production = factory.WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Production");
                    builder.UseSetting("DataProtection:Path", directory);
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IDataProtectionProvider>();
                        services.AddDataProtection();
                    });
                });
                using var client = production.CreateClient(new() { BaseAddress = new("http://localhost"), AllowAutoRedirect = false });
                using var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var protector = production.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("restart-test");
                if (start == 0)
                {
                    protectedText = protector.Protect("persistent-token");
                    Assert.NotEmpty(Directory.GetFiles(directory, "key-*.xml"));
                }
                else
                    Assert.Equal("persistent-token", protector.Unprotect(protectedText!));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class SchemeFilter(string peer) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextRequest) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                return nextRequest(context);
            });
            next(app);
            app.UseEndpoints(endpoints => endpoints.MapGet("/test/scheme", (HttpContext context) => context.Request.Scheme));
        };
    }
}

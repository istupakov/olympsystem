using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Olymp.Domain;
using Olymp.Site.IdentityUI;
using Olymp.Site.Services.Mail;

using Xunit;

namespace Olymp.IntegrationTests.Site;

public sealed class SiteFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _connectionString;
    private bool _databaseCreated;

    public SiteFactory()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? throw new InvalidOperationException(
                "Set ConnectionStrings__DefaultConnection to SQL Server. Tests create a separate temporary database.");
        var connection = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = $"OlympTests_{Guid.NewGuid():N}"
        };
        _connectionString = connection.ConnectionString;
    }

    public SiteData Data { get; private set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connectionString,
                ["MailService:From"] = "tests@example.test",
                ["MailService:SmtpServer"] = "localhost",
                ["MailService:Port"] = "587",
                ["MailService:UserName"] = "unused",
                ["MailService:Password"] = "unused"
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<OlympContext>>();
            services.AddDbContext<OlympContext>(options => options.UseSqlServer(_connectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTrackingWithIdentityResolution));
            services.RemoveAll<IEmailSender>();
            services.AddTransient<IEmailSender, DebugEmailSender>();
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
        });
    }

    public async ValueTask InitializeAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        _databaseCreated = true;
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        Data = await SiteData.SeedAsync(scope.ServiceProvider);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (_databaseCreated)
            {
                await using var scope = Services.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
                await context.Database.EnsureDeletedAsync(CancellationToken.None);
                _databaseCreated = false;
            }
        }
        finally
        {
            await base.DisposeAsync();
        }
    }

    public HttpClient CreateSiteClient()
    {
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
    }

    public async Task<HttpClient> LoginAsync(string username)
    {
        var client = CreateSiteClient();
        using var response = await PostFormAsync(client, "/Identity/Account/Login", new()
        {
            ["Input.Username"] = username,
            ["Input.Password"] = SiteData.Password
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        return client;
    }

    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path,
        Dictionary<string, string> fields, string? tokenPage = null)
    {
        using var page = await client.GetAsync(tokenPage ?? path, TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = await new HtmlParser().ParseDocumentAsync(html, TestContext.Current.CancellationToken);
        fields["__RequestVerificationToken"] = document.QuerySelector("input[name='__RequestVerificationToken']")
            ?.GetAttribute("value") ?? throw new InvalidOperationException("Antiforgery token not found in the form.");
        using var content = new FormUrlEncodedContent(fields);
        return await client.PostAsync(path, content, TestContext.Current.CancellationToken);
    }
}

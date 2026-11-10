using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Components;
using Olymp.WebApp.Services.Mail;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

public sealed class WebAppFactory : WebApplicationFactory<App>, IAsyncLifetime
{
    public const string Password = "TestPassword123";

    private readonly string _connectionString;
    private bool _databaseCreated;

    internal RecordingEmailSender EmailSender { get; } = new();

    public WebAppFactory()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? throw new InvalidOperationException("Set ConnectionStrings__DefaultConnection. Tests create a separate temporary database.");
        _connectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = $"OlympWebAppTests_{Guid.NewGuid():N}"
        }.ConnectionString;
    }

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
            services.AddDbContextFactory<OlympContext>(options => options.UseSqlServer(_connectionString));
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(EmailSender);
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddSingleton<IStartupFilter, TestEndpointsStartupFilter>();
        });
    }

    private sealed class TestEndpointsStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.UseEndpoints(endpoints => endpoints.MapGet("/test/auth", context => context.Response.WriteAsync(context.User.Identity?.Name ?? "")).RequireAuthorization());
        };
    }

    public async ValueTask InitializeAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        _databaseCreated = true;
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (_databaseCreated)
            {
                await using var scope = Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<OlympContext>().Database.EnsureDeletedAsync(CancellationToken.None);
                _databaseCreated = false;
            }
        }
        finally
        {
            await base.DisposeAsync();
        }
    }

    public async Task<OlympUser> AddUserAsync(bool legacyLogin = false, bool emailConfirmed = true)
    {
        string id = Guid.NewGuid().ToString("N");
        string email = $"{id}@example.test";
        var user = new OlympUser
        {
            Name = "Test user",
            UserName = legacyLogin ? $"legacy_{id}" : email,
            Email = email,
            EmailConfirmed = emailConfirmed,
            Memberships = [],
            Teams = [],
            Submissions = [],
            Messages = []
        };
        await using var scope = Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(user, Password)).Succeeded);
        return user;
    }

    public HttpClient CreateSiteClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });

    public async Task<HttpClient> LoginAsync(string username)
    {
        var client = CreateSiteClient();
        using var response = await PostFormAsync(client, "/Account/Login?ReturnUrl=%2Ftest%2Fauth", "login", new()
        {
            ["Input.Login"] = username,
            ["Input.Password"] = Password
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, string formName,
        Dictionary<string, string> fields)
    {
        using var page = await client.GetAsync(path, TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var form = Assert.Single(document.QuerySelectorAll("form"),
            form => form.QuerySelector("input[name='_handler']")?.GetAttribute("value") == formName);
        foreach (var input in form.QuerySelectorAll("input[type='hidden'][name]"))
            fields[input.GetAttribute("name")!] = input.GetAttribute("value") ?? "";

        using var content = new FormUrlEncodedContent(fields);
        return await client.PostAsync(path, content, TestContext.Current.CancellationToken);
    }
}

internal sealed class RecordingEmailSender : IEmailSender
{
    public List<(string Email, string Subject, string Html)> Messages { get; } = [];
    public List<(string Email, string Link)> Confirmations { get; } = [];
    public List<(string Email, string Link)> PasswordResets { get; } = [];

    public Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        Messages.Add((email, subject, htmlMessage));
        using var document = new HtmlParser().ParseDocument(htmlMessage);
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            string link = anchor.GetAttribute("href")!;
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
                continue;
            if (uri.AbsolutePath is "/Account/ConfirmEmail" or "/Account/ConfirmEmailChange")
                Confirmations.Add((email, link));
            else if (uri.AbsolutePath == "/Account/ResetPassword")
                PasswordResets.Add((email, link));
        }
        return Task.CompletedTask;
    }
}

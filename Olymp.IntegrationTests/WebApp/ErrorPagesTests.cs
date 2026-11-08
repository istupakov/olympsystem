using System.Data.Common;
using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class ErrorPagesTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("ru", false)]
    [InlineData("ru", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public async Task ProductionErrorIsLocalizedAndDoesNotQueryDatabase(string culture, bool authenticated)
    {
        var user = authenticated ? await factory.AddUserAsync() : null;
        var database = new RejectQueriesInterceptor();
        await using var site = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter, FailingEndpointStartupFilter>();
                services.ConfigureDbContext<OlympContext>(options => options.AddInterceptors(database));
            });
        });
        using var client = site.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        if (user is not null)
        {
            using var login = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new()
            {
                ["Input.Login"] = user.UserName!,
                ["Input.Password"] = WebAppFactory.Password
            });
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        }

        database.RejectQueries = true;
        using var response = await client.GetAsync("/test/error", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = await new HtmlParser().ParseDocumentAsync(html, TestContext.Current.CancellationToken);
        Assert.Equal(culture == "ru" ? "Ошибка" : "Error", document.Title);
        Assert.Equal(document.Title, document.QuerySelector("main h1")!.TextContent);
        Assert.Equal(culture == "ru"
            ? "Не удалось обработать запрос. Попробуйте ещё раз позже."
            : "We couldn't process your request. Please try again later.", document.QuerySelector("main p")!.TextContent);
        Assert.Equal(culture == "ru" ? "Идентификатор запроса:" : "Request ID:", document.QuerySelector("main strong")!.TextContent);
        Assert.False(string.IsNullOrWhiteSpace(document.QuerySelector("main code")!.TextContent));
        Assert.Equal(culture == "ru" ? "На главную" : "Go to the home page", document.QuerySelector("main a[href='']")!.TextContent);
        Assert.Equal(culture, document.DocumentElement.GetAttribute("lang"));
        Assert.Null(document.QuerySelector("header"));
        Assert.DoesNotContain("PRIVATE_EXCEPTION_DETAILS", html);
        Assert.DoesNotContain("Development", html);
        Assert.Equal(0, database.RejectedQueries);
    }

    [Theory]
    [InlineData("ru", "Страница не найдена")]
    [InlineData("en", "Not Found")]
    public async Task MissingResourcesRenderLocalizedNotFoundPage(string culture, string title)
    {
        var user = await factory.AddUserAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        foreach (string path in new[]
        {
            "/contests/2147483647/competitors",
            "/contests/2147483647/standing",
            "/contests/2147483647/rules",
            "/contests/2147483647/problems",
            "/contests/2147483647/registration",
            "/submissions/2147483647",
            "/Account/ConfirmEmail?userId=2147483647&code=invalid",
            "/Account/RegisterConfirmation?email=missing-user%40example.test"
        })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var document = await new HtmlParser().ParseDocumentAsync(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            Assert.Equal(title, document.Title);
            Assert.Equal(title, Assert.Single(document.QuerySelectorAll("main h1")).TextContent);
        }
    }

    private sealed class FailingEndpointStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.UseEndpoints(endpoints => endpoints.MapGet("/test/error", _ => throw new InvalidOperationException("PRIVATE_EXCEPTION_DETAILS")));
        };
    }

    private sealed class RejectQueriesInterceptor : DbCommandInterceptor
    {
        public bool RejectQueries { get; set; }
        public int RejectedQueries { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            RejectIfEnabled();
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            RejectIfEnabled();
            return ValueTask.FromResult(result);
        }

        private void RejectIfEnabled()
        {
            if (!RejectQueries)
                return;
            RejectedQueries++;
            throw new InvalidOperationException("Database is unavailable for this test.");
        }
    }
}

using System.Net;
using System.Text;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class RegistrationTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task RegistrationWithRulesAcceptedCreatesAccountAndRequiresConfirmation(string culture)
    {
        string email = $"{Guid.NewGuid():N}@example.test";
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await RegisterAsync(client, email);
        Assert.Equal("/Account/RegisterConfirmation", RedirectTarget(response).AbsolutePath);

        using (var scope = factory.Services.CreateScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<OlympContext>().OlympUsers
                .SingleAsync(u => u.Email == email, TestContext.Current.CancellationToken);
            Assert.Equal("New user", user.Name);
            Assert.Equal(email, user.UserName);
            Assert.False(user.EmailConfirmed);
            Assert.Null(user.FirstName);
            Assert.Null(user.LastName);
            Assert.Null(user.OrganizationId);
        }
        using (var restricted = await client.GetAsync("/test/auth", TestContext.Current.CancellationToken))
            Assert.Equal("/Account/Login", RedirectTarget(restricted).AbsolutePath);

        var (_, link) = Assert.Single(factory.EmailSender.Confirmations, letter => letter.Email == email);
        using var confirmed = await client.GetAsync(WebUtility.HtmlDecode(link), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await confirmed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(culture == "ru" ? "Подтверждение email" : "Confirm email", document.Title);
        Assert.Equal(culture == "ru" ? "Спасибо за подтверждение email." : "Thank you for confirming your email.",
            document.QuerySelector(".alert-success")!.TextContent.Trim());
        using var signedIn = await factory.LoginAsync(email);
        using var authorized = await signedIn.GetAsync("/test/auth", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task RegistrationRejectsEmailOwnedByLegacyUsernameIgnoringCase(string culture)
    {
        var existing = await factory.AddUserAsync(legacyLogin: true);
        int lettersBefore = factory.EmailSender.Confirmations.Count;
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await RegisterAsync(client, existing.Email!.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        string email = existing.Email!.ToUpperInvariant();
        Assert.Equal(culture == "ru" ? $"Email «{email}» уже занят." : $"Email '{email}' is already taken.",
            document.QuerySelector(".alert-danger")!.TextContent.Trim());
        Assert.Equal(lettersBefore, factory.EmailSender.Confirmations.Count);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OlympContext>().OlympUsers
            .CountAsync(u => u.NormalizedEmail == existing.NormalizedEmail, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConfirmationCanBeResentWithoutSigningIn()
    {
        var user = await factory.AddUserAsync(emailConfirmed: false);
        using var client = factory.CreateSiteClient();
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/ResendEmailConfirmation", "resend-email-confirmation",
            new() { ["Input.Email"] = user.Email! });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (_, link) = Assert.Single(factory.EmailSender.Confirmations, letter => letter.Email == user.Email);
        using var confirmed = await client.GetAsync(WebUtility.HtmlDecode(link), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByIdAsync(user.Id.ToString()))!.EmailConfirmed);
    }

    [Theory]
    [InlineData("ru", false)]
    [InlineData("ru", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public async Task InvalidConfirmationLinkDoesNotConfirmAccount(string culture, bool malformed)
    {
        var user = await factory.AddUserAsync(emailConfirmed: false);
        string code = malformed ? "!" : WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("invalid-token"));
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync(QueryHelpers.AddQueryString("/Account/ConfirmEmail",
            new Dictionary<string, string?> { ["userId"] = user.Id.ToString(), ["code"] = code }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(culture == "ru" ? "Не удалось подтвердить email." : "Error confirming your email.",
            document.QuerySelector(".alert-danger")!.TextContent.Trim());
        using var scope = factory.Services.CreateScope();
        Assert.False((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByIdAsync(user.Id.ToString()))!.EmailConfirmed);
    }

    [Fact]
    public async Task ExpiredConfirmationLinkDoesNotConfirmAccount()
    {
        var user = await factory.AddUserAsync(emailConfirmed: false);
        await using var expired = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromMinutes(-1))));
        using var client = expired.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var request = await WebAppFactory.PostFormAsync(client, "/Account/ResendEmailConfirmation", "resend-email-confirmation",
            new() { ["Input.Email"] = user.Email! });
        request.EnsureSuccessStatusCode();
        var (_, link) = Assert.Single(factory.EmailSender.Confirmations, letter => letter.Email == user.Email);
        using var response = await client.GetAsync(WebUtility.HtmlDecode(link), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("alert-danger", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var scope = factory.Services.CreateScope();
        Assert.False((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByIdAsync(user.Id.ToString()))!.EmailConfirmed);
    }

    [Theory]
    [InlineData("/Account/ConfirmEmail", "/")]
    [InlineData("/Account/ConfirmEmailChange", "/Account/Login")]
    [InlineData("/Account/ResetPassword", "/Account/InvalidPasswordReset")]
    public async Task IncompleteLinkRedirectsToExistingErrorFlow(string path, string expected)
    {
        using var client = factory.CreateSiteClient();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(expected, RedirectTarget(response).AbsolutePath);
    }

    [Fact]
    public async Task RegistrationConfirmationForUnknownAccountReturnsNotFound()
    {
        using var client = factory.CreateSiteClient();
        using var response = await client.GetAsync($"/Account/RegisterConfirmation?email={Guid.NewGuid():N}%40example.test",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("ru", null)]
    [InlineData("ru", "false")]
    [InlineData("en", null)]
    [InlineData("en", "false")]
    public async Task RegistrationRequiresRulesAcceptance(string culture, string? acceptance)
    {
        string email = $"{Guid.NewGuid():N}@example.test";
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        int lettersBefore = factory.EmailSender.Confirmations.Count;
        var fields = new Dictionary<string, string>
        {
            ["Input.PublicName"] = "New user",
            ["Input.Email"] = email,
            ["Input.Password"] = WebAppFactory.Password,
            ["Input.ConfirmPassword"] = WebAppFactory.Password
        };
        if (acceptance is not null)
            fields["Input.AcceptRules"] = acceptance;

        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Register", "register", fields);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Contains(culture == "ru" ? "Необходимо принять правила использования системы." : "You must accept the system usage rules.",
            document.QuerySelectorAll(".validation-message").Select(element => element.TextContent));
        Assert.NotNull(document.QuerySelector("input[type='checkbox'][name='Input.AcceptRules']:not([checked])"));
        Assert.NotNull(document.QuerySelector("label[for='Input.AcceptRules'] a[href='rules']"));
        Assert.Equal(lettersBefore, factory.EmailSender.Confirmations.Count);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OlympContext>().Users
            .AnyAsync(user => user.Email == email, TestContext.Current.CancellationToken));
    }

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email) =>
        WebAppFactory.PostFormAsync(client, "/Account/Register", "register", new()
        {
            ["Input.PublicName"] = "New user",
            ["Input.Email"] = email,
            ["Input.Password"] = WebAppFactory.Password,
            ["Input.AcceptRules"] = "true",
            ["Input.ConfirmPassword"] = WebAppFactory.Password
        });

    private static Uri RedirectTarget(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var target = new Uri(new Uri("https://localhost"), response.Headers.Location);
        Assert.Equal("localhost", target.Host);
        return target;
    }
}

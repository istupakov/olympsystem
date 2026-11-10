using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain.Models;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class PasswordTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    private const string NewPassword = "ChangedPassword456";

    [Fact]
    public async Task LoginWithLegacyPasswordUpgradesHash()
    {
        const string legacyHash = "Uzy20PJha0wXWg+TVIpe5IlkKxI=|SHA1|AQIDBAUGBwg=";
        var user = await factory.AddUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var saved = await users.FindByIdAsync(user.Id.ToString());
            Assert.NotNull(saved);
            saved.PasswordHash = legacyHash;
            Assert.True((await users.UpdateAsync(saved)).Succeeded);
        }

        using var client = await factory.LoginAsync(user.UserName!);
        using var response = await client.GetAsync("/test/auth", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verification = factory.Services.CreateScope();
        var manager = verification.ServiceProvider.GetRequiredService<UserManager<User>>();
        var upgraded = await manager.FindByIdAsync(user.Id.ToString());
        Assert.NotNull(upgraded);
        Assert.NotEqual(legacyHash, upgraded.PasswordHash);
        Assert.True(await manager.CheckPasswordAsync(upgraded, WebAppFactory.Password));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetRequestDoesNotRevealUnknownOrUnconfirmedAccount(bool existing)
    {
        string email = existing ? (await factory.AddUserAsync(emailConfirmed: false)).Email! : $"{Guid.NewGuid():N}@example.test";
        int lettersBefore = factory.EmailSender.PasswordResets.Count;
        using var client = factory.CreateSiteClient();
        using var response = await RequestResetAsync(client, email);

        Assert.Equal("/Account/ForgotPasswordConfirmation", RedirectTarget(response).AbsolutePath);
        Assert.Equal(lettersBefore, factory.EmailSender.PasswordResets.Count);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task ResetChangesPasswordAndCannotBeReused(string culture)
    {
        var user = await factory.AddUserAsync();
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var request = await RequestResetAsync(client, user.Email!);
        Assert.Equal("/Account/ForgotPasswordConfirmation", RedirectTarget(request).AbsolutePath);
        var (_, link) = Assert.Single(factory.EmailSender.PasswordResets, letter => letter.Email == user.Email);

        using var response = await ResetAsync(client, link, user.Email!, NewPassword);
        Assert.Equal("/Account/ResetPasswordConfirmation", RedirectTarget(response).AbsolutePath);
        await AssertPasswordAsync(user, NewPassword, WebAppFactory.Password);

        using var replay = await ResetAsync(client, link, user.Email!, "AnotherPassword789");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(culture == "ru" ? "Недействительный токен." : "Invalid token.",
            document.QuerySelector(".alert-danger")!.TextContent.Trim());
        await AssertPasswordAsync(user, NewPassword, "AnotherPassword789");
    }

    [Fact]
    public async Task ResetLinkCannotBeUsedForAnotherAccount()
    {
        var owner = await factory.AddUserAsync();
        var other = await factory.AddUserAsync();
        using var client = factory.CreateSiteClient();
        using var request = await RequestResetAsync(client, owner.Email!);
        Assert.Equal("/Account/ForgotPasswordConfirmation", RedirectTarget(request).AbsolutePath);
        var (_, link) = Assert.Single(factory.EmailSender.PasswordResets, letter => letter.Email == owner.Email);
        using var response = await ResetAsync(client, link, other.Email!, NewPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("alert-danger", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await AssertPasswordAsync(owner, WebAppFactory.Password, NewPassword);
        await AssertPasswordAsync(other, WebAppFactory.Password, NewPassword);
    }

    [Fact]
    public async Task ExpiredResetLinkDoesNotChangePassword()
    {
        var user = await factory.AddUserAsync();
        await using var expired = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromMinutes(-1))));
        using var client = expired.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var request = await RequestResetAsync(client, user.Email!);
        Assert.Equal("/Account/ForgotPasswordConfirmation", RedirectTarget(request).AbsolutePath);
        var (_, link) = Assert.Single(factory.EmailSender.PasswordResets, letter => letter.Email == user.Email);
        using var response = await ResetAsync(client, link, user.Email!, NewPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("alert-danger", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await AssertPasswordAsync(user, WebAppFactory.Password, NewPassword);
    }

    [Fact]
    public async Task MalformedResetLinkRedirectsToInvalidLinkPage()
    {
        using var client = factory.CreateSiteClient();
        using var response = await client.GetAsync("/Account/ResetPassword?code=%21", TestContext.Current.CancellationToken);
        Assert.Equal("/Account/InvalidPasswordReset", RedirectTarget(response).AbsolutePath);
    }

    [Theory]
    [InlineData("ru", "Неверный пароль.")]
    [InlineData("en", "Incorrect password.")]
    public async Task ChangingPasswordRequiresCurrentPassword(string culture, string message)
    {
        var user = await factory.AddUserAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Manage/ChangePassword", "change-password", new()
        {
            ["Input.OldPassword"] = "WrongPassword123",
            ["Input.NewPassword"] = NewPassword,
            ["Input.ConfirmPassword"] = NewPassword
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(message, document.QuerySelector(".alert-danger")!.TextContent.Trim());
        await AssertPasswordAsync(user, WebAppFactory.Password, NewPassword);
    }

    [Fact]
    public async Task PasswordChangeRefreshesCurrentSessionAndRevokesOtherSession()
    {
        var user = await factory.AddUserAsync();
        await using var site = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero)));
        using var current = site.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var other = site.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        foreach (var client in new[] { current, other })
        {
            using var login = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new()
            {
                ["Input.Login"] = user.UserName!,
                ["Input.Password"] = WebAppFactory.Password
            });
            Assert.Equal("/", RedirectTarget(login).AbsolutePath);
        }
        using var response = await WebAppFactory.PostFormAsync(current, "/Account/Manage/ChangePassword", "change-password", new()
        {
            ["Input.OldPassword"] = WebAppFactory.Password,
            ["Input.NewPassword"] = NewPassword,
            ["Input.ConfirmPassword"] = NewPassword
        });

        Assert.Equal("/Account/Manage/ChangePassword", RedirectTarget(response).AbsolutePath);
        using var authorized = await current.GetAsync("/test/auth", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        using var revoked = await other.GetAsync("/test/auth", TestContext.Current.CancellationToken);
        Assert.Equal("/Account/Login", RedirectTarget(revoked).AbsolutePath);
        await AssertPasswordAsync(user, NewPassword, WebAppFactory.Password);
    }

    private async Task AssertPasswordAsync(OlympUser user, string accepted, string rejected)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var saved = await users.FindByIdAsync(user.Id.ToString());
        Assert.NotNull(saved);
        Assert.True(await users.CheckPasswordAsync(saved, accepted));
        Assert.False(await users.CheckPasswordAsync(saved, rejected));
    }

    private static Task<HttpResponseMessage> RequestResetAsync(HttpClient client, string email) =>
        WebAppFactory.PostFormAsync(client, "/Account/ForgotPassword", "forgot-password", new() { ["Input.Email"] = email });

    private static Task<HttpResponseMessage> ResetAsync(HttpClient client, string link, string email, string password) =>
        WebAppFactory.PostFormAsync(client, WebUtility.HtmlDecode(link), "reset-password", new()
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = password
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

using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain.Models;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class EmailLocalizationTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task RegistrationSendsLocalizedConfirmationWithWorkingLink(string culture)
    {
        string email = $"{Guid.NewGuid():N}@example.test";
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Register", "register", new()
        {
            ["Input.PublicName"] = "New user",
            ["Input.Email"] = email,
            ["Input.Password"] = WebAppFactory.Password,
            ["Input.ConfirmPassword"] = WebAppFactory.Password
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var (_, subject, html) = Assert.Single(factory.EmailSender.Messages, letter => letter.Email == email);
        var link = AssertConfirmationEmail(subject, html, culture, "/Account/ConfirmEmail");
        using var confirmed = await client.GetAsync(link, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        using var scope = factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByEmailAsync(email);
        Assert.True(user!.EmailConfirmed);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task EmailChangeUsesLocalizedConfirmationTemplateAndNewAddress(string culture)
    {
        var user = await factory.AddUserAsync();
        string email = $"{Guid.NewGuid():N}@example.test";
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Manage/Email", "change-email",
            new() { ["Input.NewEmail"] = email });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (_, subject, html) = Assert.Single(factory.EmailSender.Messages, letter => letter.Email == email);
        var link = AssertConfirmationEmail(subject, html, culture, "/Account/ConfirmEmailChange");
        Assert.Equal(email, QueryHelpers.ParseQuery(link.Query)["email"].ToString());
        using var confirmed = await client.GetAsync(link, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        using var scope = factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByIdAsync(user.Id.ToString());
        Assert.Equal(email, saved!.Email);
        Assert.Equal(email, saved.UserName);
        Assert.True(saved.EmailConfirmed);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task PasswordResetSendsLocalizedEmailWithWorkingLink(string culture)
    {
        var user = await factory.AddUserAsync();
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/ForgotPassword", "forgot-password",
            new() { ["Input.Email"] = user.Email! });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var (_, subject, html) = Assert.Single(factory.EmailSender.Messages, letter => letter.Email == user.Email);
        Assert.Equal(culture == "ru" ? "Сбросьте пароль" : "Reset your password", subject);
        var link = ReadLink(html, culture, "/Account/ResetPassword");
        string encodedLink = HtmlEncoder.Default.Encode(link.AbsoluteUri);
        Assert.Equal(culture == "ru"
            ? $"Чтобы сбросить пароль, <a href='{encodedLink}'>нажмите здесь</a>. Если вы не запрашивали сброс пароля, проигнорируйте это письмо."
            : $"Please reset your password by <a href='{encodedLink}'>clicking here</a>. If you didn't request a password reset, you can ignore this email.", html);
        using var reset = await WebAppFactory.PostFormAsync(client, link.AbsoluteUri, "reset-password", new()
        {
            ["Input.Email"] = user.Email!,
            ["Input.Password"] = "ChangedPassword456",
            ["Input.ConfirmPassword"] = "ChangedPassword456"
        });
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var saved = await users.FindByIdAsync(user.Id.ToString());
        Assert.True(await users.CheckPasswordAsync(saved!, "ChangedPassword456"));
        Assert.False(await users.CheckPasswordAsync(saved!, WebAppFactory.Password));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task PasswordResetCodeEmailIsLocalized(string culture)
    {
        var user = await factory.AddUserAsync();
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUICulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var scope = factory.Services.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<IEmailSender<User>>();
            await sender.SendPasswordResetCodeAsync(user, user.Email!, "123456");
            var (_, subject, html) = Assert.Single(factory.EmailSender.Messages, letter => letter.Email == user.Email);
            Assert.Equal(culture == "ru" ? "Сбросьте пароль" : "Reset your password", subject);
            Assert.Equal(culture == "ru"
                ? "Для сброса пароля используйте код: 123456. Если вы не запрашивали сброс пароля, проигнорируйте это письмо."
                : "Please reset your password using the following code: 123456. If you didn't request a password reset, you can ignore this email.", html);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUICulture;
        }
    }

    private static Uri AssertConfirmationEmail(string subject, string html, string culture, string path)
    {
        Assert.Equal(culture == "ru" ? "Подтвердите email" : "Confirm your email", subject);
        var link = ReadLink(html, culture, path);
        string encodedLink = HtmlEncoder.Default.Encode(link.AbsoluteUri);
        Assert.Equal(culture == "ru"
            ? $"Чтобы подтвердить учётную запись, <a href='{encodedLink}'>нажмите здесь</a>. Если вы не запрашивали подтверждение email, проигнорируйте это письмо."
            : $"Please confirm your account by <a href='{encodedLink}'>clicking here</a>. If you didn't request this email confirmation, you can ignore this email.", html);
        Assert.Contains("&amp;", html);
        Assert.DoesNotContain("&amp;amp;", html);
        Assert.False(string.IsNullOrWhiteSpace(QueryHelpers.ParseQuery(link.Query)["userId"]));
        return link;
    }

    private static Uri ReadLink(string html, string culture, string path)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var anchor = Assert.Single(document.QuerySelectorAll("a"));
        Assert.Equal(culture == "ru" ? "нажмите здесь" : "clicking here", anchor.TextContent);
        var uri = new Uri(anchor.GetAttribute("href")!);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("localhost", uri.Host);
        Assert.Equal(path, uri.AbsolutePath);
        Assert.False(string.IsNullOrWhiteSpace(QueryHelpers.ParseQuery(uri.Query)["code"]));
        return uri;
    }
}

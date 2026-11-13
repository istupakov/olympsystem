using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Identity;

[Trait("Category", "Integration")]
public class EmailSettingsTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("ru", "Настройки email", "Новый email", "Введите новый email", "Изменить email")]
    [InlineData("en", "Manage email", "New email", "Enter a new email", "Change email")]
    public async Task EmailSettingsLocalizesTextAndKeepsCurrentAddressReadOnly(string culture, string title,
        string newEmailLabel, string placeholder, string button)
    {
        var user = await factory.AddUserAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/Account/Manage/Email", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.Equal(title, document.Title);
        Assert.Equal(title, document.QuerySelector("h3")!.TextContent);
        Assert.Equal(user.Email, document.QuerySelector("input#email[disabled]")!.GetAttribute("value"));
        Assert.Equal(newEmailLabel, document.QuerySelector("label[for='Input.NewEmail']")!.TextContent.Trim());
        var input = document.QuerySelector("input[name='Input.NewEmail']")!;
        Assert.Equal(placeholder, input.GetAttribute("placeholder"));
        Assert.Equal(user.Email, input.GetAttribute("value"));
        Assert.Equal(button, document.QuerySelector("form:has(input[name='Input.NewEmail']) button[type='submit']")!.TextContent);
        Assert.Null(document.QuerySelector(".alert-warning"));
    }

    [Theory]
    [InlineData("ru", "Для доступа к страницам, требующим авторизации, используйте email в качестве логина.", "Теперь ваш email используется в качестве логина.")]
    [InlineData("en", "To access pages that require sign-in, use your email address as your username.", "Your email address is now your username.")]
    public async Task LegacyLoginWarningAndMigrationResultAreLocalized(string culture, string warning, string message)
    {
        var user = await factory.AddUserAsync(legacyLogin: true);
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var page = await client.GetAsync("/Account/Manage/Email", TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var migration = document.QuerySelector(".alert-warning")!;
        Assert.Contains(warning, migration.TextContent);
        Assert.Equal(user.UserName, migration.QuerySelector("strong")!.TextContent);
        Assert.Equal(culture == "ru" ? $"Использовать {user.Email} для входа" : $"Use {user.Email} for sign-in",
            migration.QuerySelector("button")!.TextContent);
        int lettersBefore = factory.EmailSender.Confirmations.Count;
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Manage/Email", "use-email-login", new());

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var result = await client.GetAsync(response.Headers.Location, TestContext.Current.CancellationToken);
        await AssertSuccessMessageAsync(result, message);
        using var resultDocument = await new HtmlParser().ParseDocumentAsync(
            await result.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Null(resultDocument.QuerySelector(".alert-warning"));
        Assert.Equal(lettersBefore, factory.EmailSender.Confirmations.Count);
        using var scope = factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByIdAsync(user.Id.ToString());
        Assert.Equal(user.Email, saved!.UserName);
        Assert.Equal(user.PasswordHash, saved.PasswordHash);
    }

    [Theory]
    [InlineData("ru", "Ваш email не изменился.", "Ссылка для подтверждения смены email отправлена. Проверьте почту.")]
    [InlineData("en", "Your email is unchanged.", "Confirmation link to change email sent. Please check your email.")]
    public async Task EmailChangeMessagesAreLocalizedAndAddressWaitsForConfirmation(string culture, string unchanged, string sent)
    {
        var user = await factory.AddUserAsync(legacyLogin: true);
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        int lettersBefore = factory.EmailSender.Confirmations.Count;
        using var noChange = await WebAppFactory.PostFormAsync(client, "/Account/Manage/Email", "change-email",
            new() { ["Input.NewEmail"] = user.Email! });
        await AssertSuccessMessageAsync(noChange, unchanged);
        Assert.Equal(lettersBefore, factory.EmailSender.Confirmations.Count);
        string newEmail = $"{Guid.NewGuid():N}@example.test";
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Manage/Email", "change-email",
            new() { ["Input.NewEmail"] = newEmail });

        await AssertSuccessMessageAsync(response, sent);
        var (_, link) = Assert.Single(factory.EmailSender.Confirmations, letter => letter.Email == newEmail);
        Assert.Contains("/Account/ConfirmEmailChange?", link);
        using var scope = factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByIdAsync(user.Id.ToString());
        Assert.Equal(user.Email, saved!.Email);
        Assert.Equal(user.UserName, saved.UserName);
        using var confirmed = await client.GetAsync(WebUtility.HtmlDecode(link), TestContext.Current.CancellationToken);
        await AssertSuccessMessageAsync(confirmed, culture == "ru"
            ? "Спасибо за подтверждение смены email." : "Thank you for confirming your email change.");
        using var confirmationDocument = await new HtmlParser().ParseDocumentAsync(
            await confirmed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(culture == "ru" ? "Подтверждение смены email" : "Confirm email change", confirmationDocument.Title);
        using var finalScope = factory.Services.CreateScope();
        var updated = await finalScope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByIdAsync(user.Id.ToString());
        Assert.Equal(newEmail, updated!.Email);
        Assert.Equal(newEmail, updated.UserName);
    }

    [Theory]
    [InlineData("ru", "", "Поле «Новый email» обязательно для заполнения.")]
    [InlineData("en", "", "The New email field is required.")]
    [InlineData("ru", "not-an-email", "Поле «Новый email» должно содержать корректный адрес электронной почты.")]
    [InlineData("en", "not-an-email", "The New email field is not a valid e-mail address.")]
    public async Task EmailValidationUsesLocalizedFieldName(string culture, string newEmail, string message)
    {
        var user = await factory.AddUserAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        int lettersBefore = factory.EmailSender.Confirmations.Count;
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Manage/Email", "change-email",
            new() { ["Input.NewEmail"] = newEmail });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(message, Assert.Single(document.QuerySelectorAll(".validation-message")).TextContent);
        Assert.Equal(lettersBefore, factory.EmailSender.Confirmations.Count);
    }

    [Theory]
    [InlineData("ru", "Отправить письмо подтверждения", "Письмо подтверждения отправлено. Проверьте почту.")]
    [InlineData("en", "Send verification email", "Verification email sent. Please check your email.")]
    public async Task VerificationCanBeSentFromExistingSessionInSelectedLanguage(string culture, string button, string message)
    {
        var user = await factory.AddUserAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        // An existing session can outlive an administrative change of the confirmation flag.
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var saved = await users.FindByIdAsync(user.Id.ToString());
            saved!.EmailConfirmed = false;
            Assert.True((await users.UpdateAsync(saved)).Succeeded);
        }
        using var page = await client.GetAsync("/Account/Manage/Email", TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(button, document.QuerySelector("button[form='send-verification-form']")!.TextContent);
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Manage/Email", "send-verification", new());

        await AssertSuccessMessageAsync(response, message);
        var (_, link) = Assert.Single(factory.EmailSender.Confirmations, letter => letter.Email == user.Email);
        Assert.Contains("/Account/ConfirmEmail?", link);
    }

    private static async Task AssertSuccessMessageAsync(HttpResponseMessage response, string message)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(message, document.QuerySelector(".alert-success")!.TextContent.Trim());
        Assert.Null(document.QuerySelector(".alert-danger"));
    }
}

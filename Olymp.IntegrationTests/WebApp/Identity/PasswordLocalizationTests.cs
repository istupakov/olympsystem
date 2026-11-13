using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Identity;

[Trait("Category", "Integration")]
public class PasswordLocalizationTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task ForgotPasswordFormIsLocalized(string culture)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/Account/ForgotPassword", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        bool russian = culture == "ru";
        Assert.Equal(russian ? "Забыли пароль?" : "Forgot your password?", document.Title);
        Assert.Equal(document.Title, document.QuerySelector("h1")!.TextContent);
        Assert.Equal(russian ? "Введите email." : "Enter your email.", document.QuerySelector("h2")!.TextContent);
        Assert.Equal("Email", document.QuerySelector("label[for='Input.Email']")!.TextContent.Trim());
        Assert.Equal("name@example.com", document.QuerySelector("input[name='Input.Email']")!.GetAttribute("placeholder"));
        Assert.Equal(russian ? "Сброс пароля" : "Reset password",
            document.QuerySelector("form:has(input[name='Input.Email']) button[type='submit']")!.TextContent);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task ResetPasswordFormIsLocalized(string culture)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/Account/ResetPassword?code=dGVzdA", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        bool russian = culture == "ru";
        Assert.Equal(russian ? "Сброс пароля" : "Reset password", document.Title);
        Assert.Equal(document.Title, document.QuerySelector("h1")!.TextContent);
        Assert.Equal(russian ? "Сбросьте пароль." : "Reset your password.", document.QuerySelector("h2")!.TextContent);
        Assert.Equal("Email", document.QuerySelector("label[for='Input.Email']")!.TextContent.Trim());
        Assert.Equal(russian ? "Пароль" : "Password", document.QuerySelector("label[for='Input.Password']")!.TextContent.Trim());
        Assert.Equal(russian ? "Подтверждение пароля" : "Confirm password",
            document.QuerySelector("label[for='Input.ConfirmPassword']")!.TextContent.Trim());
        foreach (string field in new[] { "Password", "ConfirmPassword" })
            Assert.Equal(russian ? "пароль" : "password", document.QuerySelector($"input[name='Input.{field}']")!.GetAttribute("placeholder"));
        Assert.Equal(russian ? "Сбросить" : "Reset",
            document.QuerySelector("form:has(input[name='Input.Email']) button[type='submit']")!.TextContent);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task ChangePasswordFormIsLocalized(string culture)
    {
        var user = await factory.AddUserAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/Account/Manage/ChangePassword", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        bool russian = culture == "ru";
        Assert.Equal(russian ? "Смена пароля" : "Change password", document.Title);
        Assert.Equal(document.Title, document.QuerySelector("h3")!.TextContent);
        string[] fields = ["OldPassword", "NewPassword", "ConfirmPassword"];
        string[] labels = russian ? ["Текущий пароль", "Новый пароль", "Подтверждение нового пароля"]
            : ["Current password", "New password", "Confirm new password"];
        foreach (var (field, label) in fields.Zip(labels))
        {
            Assert.Equal(label, document.QuerySelector($"label[for='Input.{field}']")!.TextContent.Trim());
            string placeholder = field == "OldPassword"
                ? (russian ? "Введите старый пароль" : "Enter the old password")
                : (russian ? "Введите новый пароль" : "Enter the new password");
            Assert.Equal(placeholder, document.QuerySelector($"input[name='Input.{field}']")!.GetAttribute("placeholder"));
        }
        Assert.Equal(russian ? "Обновить пароль" : "Update password",
            document.QuerySelector("form:has(input[name='Input.OldPassword']) button[type='submit']")!.TextContent);
    }

    [Theory]
    [InlineData("ru", "/Account/ForgotPasswordConfirmation", "Подтверждение запроса сброса пароля", "Проверьте почту, чтобы сбросить пароль.")]
    [InlineData("en", "/Account/ForgotPasswordConfirmation", "Forgot password confirmation", "Please check your email to reset your password.")]
    [InlineData("ru", "/Account/ResetPasswordConfirmation", "Подтверждение сброса пароля", "Ваш пароль сброшен. Пожалуйста, нажмите здесь, чтобы войти.")]
    [InlineData("en", "/Account/ResetPasswordConfirmation", "Reset password confirmation", "Your password has been reset. Please click here to log in.")]
    [InlineData("ru", "/Account/InvalidPasswordReset", "Недействительный запрос сброса пароля", "Ссылка для сброса пароля недействительна.")]
    [InlineData("en", "/Account/InvalidPasswordReset", "Invalid password reset", "The password reset link is invalid.")]
    public async Task PasswordNoticesAreLocalized(string culture, string path, string title, string message)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(title, document.Title);
        Assert.Equal(title, document.QuerySelector("h1")!.TextContent);
        Assert.Equal(message, document.QuerySelector("p[role='alert']")!.TextContent.Trim());
        if (path == "/Account/ResetPasswordConfirmation")
        {
            var link = document.QuerySelector("p[role='alert'] a")!;
            Assert.Equal("account/login", link.GetAttribute("href"));
            Assert.Equal(culture == "ru" ? "нажмите здесь, чтобы войти" : "click here to log in", link.TextContent);
        }
    }

    [Theory]
    [InlineData("ru", "", "Поле «Email» обязательно для заполнения.")]
    [InlineData("en", "", "The Email field is required.")]
    [InlineData("ru", "not-an-email", "Поле «Email» должно содержать корректный адрес электронной почты.")]
    [InlineData("en", "not-an-email", "The Email field is not a valid e-mail address.")]
    public async Task ForgotPasswordValidationIsLocalizedAndDoesNotSendMail(string culture, string email, string message)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        int lettersBefore = factory.EmailSender.PasswordResets.Count;
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/ForgotPassword", "forgot-password",
            new() { ["Input.Email"] = email });
        await AssertValidationAsync(response, message);
        Assert.Equal(lettersBefore, factory.EmailSender.PasswordResets.Count);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task ResetPasswordValidationIsLocalizedAndDoesNotChangePassword(string culture)
    {
        var user = await factory.AddUserAsync();
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        bool russian = culture == "ru";
        var cases = new[]
        {
            (Field: "Email", Value: "", Message: russian ? "Поле «Email» обязательно для заполнения." : "The Email field is required."),
            (Field: "Email", Value: "invalid-email", Message: russian ? "Поле «Email» должно содержать корректный адрес электронной почты." : "The Email field is not a valid e-mail address."),
            (Field: "Password", Value: "", Message: russian ? "Поле «Пароль» обязательно для заполнения." : "The Password field is required."),
            (Field: "Password", Value: "short", Message: russian ? "Длина поля «Пароль» должна быть от 8 до 100 символов." : "The Password must be at least 8 and at max 100 characters long."),
            (Field: "Password", Value: new string('A', 101), Message: russian ? "Длина поля «Пароль» должна быть от 8 до 100 символов." : "The Password must be at least 8 and at max 100 characters long."),
            (Field: "ConfirmPassword", Value: "different", Message: russian ? "Пароль и подтверждение пароля не совпадают." : "The password and confirmation password do not match.")
        };
        foreach (var (field, value, message) in cases)
        {
            var fields = new Dictionary<string, string>
            {
                ["Input.Email"] = user.Email!,
                ["Input.Password"] = "ChangedPassword456",
                ["Input.ConfirmPassword"] = "ChangedPassword456"
            };
            fields[$"Input.{field}"] = value;
            if (field == "Password")
                fields["Input.ConfirmPassword"] = value;
            using var response = await WebAppFactory.PostFormAsync(client, "/Account/ResetPassword?code=dGVzdA", "reset-password", fields);
            await AssertValidationAsync(response, message);
        }
        await AssertPasswordUnchangedAsync(user);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task ChangePasswordValidationIsLocalizedAndDoesNotChangePassword(string culture)
    {
        var user = await factory.AddUserAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        bool russian = culture == "ru";
        var cases = new[]
        {
            (Field: "OldPassword", Value: "", Message: russian ? "Поле «Текущий пароль» обязательно для заполнения." : "The Current password field is required."),
            (Field: "NewPassword", Value: "", Message: russian ? "Поле «Новый пароль» обязательно для заполнения." : "The New password field is required."),
            (Field: "NewPassword", Value: "short", Message: russian ? "Длина поля «Новый пароль» должна быть от 8 до 100 символов." : "The New password must be at least 8 and at max 100 characters long."),
            (Field: "NewPassword", Value: new string('A', 101), Message: russian ? "Длина поля «Новый пароль» должна быть от 8 до 100 символов." : "The New password must be at least 8 and at max 100 characters long."),
            (Field: "ConfirmPassword", Value: "different", Message: russian ? "Новый пароль и подтверждение пароля не совпадают." : "The new password and confirmation password do not match.")
        };
        foreach (var (field, value, message) in cases)
        {
            var fields = new Dictionary<string, string>
            {
                ["Input.OldPassword"] = WebAppFactory.Password,
                ["Input.NewPassword"] = "ChangedPassword456",
                ["Input.ConfirmPassword"] = "ChangedPassword456"
            };
            fields[$"Input.{field}"] = value;
            if (field == "NewPassword")
                fields["Input.ConfirmPassword"] = value;
            using var response = await WebAppFactory.PostFormAsync(client, "/Account/Manage/ChangePassword", "change-password", fields);
            await AssertValidationAsync(response, message);
        }
        await AssertPasswordUnchangedAsync(user);
    }

    [Theory]
    [InlineData("ru", "Ваш пароль изменён")]
    [InlineData("en", "Your password has been changed")]
    public async Task SuccessfulPasswordChangeShowsLocalizedNotice(string culture, string message)
    {
        var user = await factory.AddUserAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Manage/ChangePassword", "change-password", new()
        {
            ["Input.OldPassword"] = WebAppFactory.Password,
            ["Input.NewPassword"] = "ChangedPassword456",
            ["Input.ConfirmPassword"] = "ChangedPassword456"
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var confirmation = await client.GetAsync(response.Headers.Location, TestContext.Current.CancellationToken);
        confirmation.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await confirmation.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var notice = document.QuerySelector(".alert-success");
        Assert.NotNull(notice);
        Assert.Equal(message, notice.TextContent.Trim());
        Assert.Null(document.QuerySelector(".alert-danger"));
    }

    private static async Task AssertValidationAsync(HttpResponseMessage response, string message)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(message, Assert.Single(document.QuerySelectorAll(".validation-message")).TextContent);
    }

    private async Task AssertPasswordUnchangedAsync(OlympUser user)
    {
        using var scope = factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByIdAsync(user.Id.ToString());
        Assert.Equal(user.PasswordHash, saved!.PasswordHash);
        Assert.Equal(user.SecurityStamp, saved.SecurityStamp);
    }
}

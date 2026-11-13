using System.Globalization;
using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.IntegrationTests.WebApp.Shared;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Identity;

[Trait("Category", "Integration")]
public class IdentityErrorTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void DescriberPreservesCodesAndEnglishMessagesAndUsesRussianResources(string culture)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUICulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var scope = factory.Services.CreateScope();
            var errors = scope.ServiceProvider.GetRequiredService<IdentityErrorDescriber>();
            var defaults = new IdentityErrorDescriber();
            var cases = new[]
            {
                (Actual: errors.DefaultError(), Default: defaults.DefaultError(), Russian: "Произошла неизвестная ошибка."),
                (Actual: errors.ConcurrencyFailure(), Default: defaults.ConcurrencyFailure(), Russian: "Не удалось сохранить изменения: объект уже изменён."),
                (Actual: errors.PasswordMismatch(), Default: defaults.PasswordMismatch(), Russian: "Неверный пароль."),
                (Actual: errors.InvalidToken(), Default: defaults.InvalidToken(), Russian: "Недействительный токен."),
                (Actual: errors.InvalidUserName("bad name"), Default: defaults.InvalidUserName("bad name"), Russian: "Логин «bad name» недопустим: разрешены только буквы и цифры."),
                (Actual: errors.InvalidEmail("bad-email"), Default: defaults.InvalidEmail("bad-email"), Russian: "Email «bad-email» недопустим."),
                (Actual: errors.DuplicateUserName("taken"), Default: defaults.DuplicateUserName("taken"), Russian: "Логин «taken» уже занят."),
                (Actual: errors.DuplicateEmail("taken@example.test"), Default: defaults.DuplicateEmail("taken@example.test"), Russian: "Email «taken@example.test» уже занят."),
                (Actual: errors.PasswordTooShort(12), Default: defaults.PasswordTooShort(12), Russian: "Пароль должен содержать не менее 12 символов."),
                (Actual: errors.PasswordRequiresUniqueChars(3), Default: defaults.PasswordRequiresUniqueChars(3), Russian: "Пароль должен содержать не менее 3 различных символов."),
                (Actual: errors.PasswordRequiresNonAlphanumeric(), Default: defaults.PasswordRequiresNonAlphanumeric(), Russian: "Пароль должен содержать хотя бы один символ, не являющийся буквой или цифрой."),
                (Actual: errors.PasswordRequiresDigit(), Default: defaults.PasswordRequiresDigit(), Russian: "Пароль должен содержать хотя бы одну цифру (от 0 до 9)."),
                (Actual: errors.PasswordRequiresLower(), Default: defaults.PasswordRequiresLower(), Russian: "Пароль должен содержать хотя бы одну строчную латинскую букву (от a до z)."),
                (Actual: errors.PasswordRequiresUpper(), Default: defaults.PasswordRequiresUpper(), Russian: "Пароль должен содержать хотя бы одну заглавную латинскую букву (от A до Z).")
            };
            foreach (var (actual, standard, russian) in cases)
            {
                Assert.Equal(standard.Code, actual.Code);
                Assert.Equal(culture == "ru" ? russian : standard.Description, actual.Description);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUICulture;
        }
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task RegistrationShowsPasswordPolicyErrorsInSelectedLanguage(string culture)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        bool russian = culture == "ru";
        var cases = new[]
        {
            (Password: "NoDigitsHere", Message: russian ? "Пароль должен содержать хотя бы одну цифру (от 0 до 9)." : "Passwords must have at least one digit ('0'-'9')."),
            (Password: "ONLYUPPER123", Message: russian ? "Пароль должен содержать хотя бы одну строчную латинскую букву (от a до z)." : "Passwords must have at least one lowercase ('a'-'z')."),
            (Password: "onlylower123", Message: russian ? "Пароль должен содержать хотя бы одну заглавную латинскую букву (от A до Z)." : "Passwords must have at least one uppercase ('A'-'Z').")
        };
        int lettersBefore = factory.EmailSender.Confirmations.Count;
        foreach (var (password, message) in cases)
        {
            string email = $"{Guid.NewGuid():N}@example.test";
            using var response = await WebAppFactory.PostFormAsync(client, "/Account/Register", "register", new()
            {
                ["Input.PublicName"] = "Test user",
                ["Input.Email"] = email,
                ["Input.Password"] = password,
                ["Input.AcceptRules"] = "true",
                ["Input.ConfirmPassword"] = password
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = await new HtmlParser().ParseDocumentAsync(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            Assert.Equal(message, document.QuerySelector(".alert-danger")!.TextContent.Trim());
            Assert.Empty(document.QuerySelectorAll(".validation-message"));
            using var scope = factory.Services.CreateScope();
            Assert.False(await scope.ServiceProvider.GetRequiredService<OlympContext>().OlympUsers
                .AnyAsync(user => user.Email == email, TestContext.Current.CancellationToken));
        }
        Assert.Equal(lettersBefore, factory.EmailSender.Confirmations.Count);
    }
}

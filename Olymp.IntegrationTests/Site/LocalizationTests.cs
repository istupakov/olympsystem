using System.Globalization;
using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Olymp.Domain;
using Olymp.Domain.Models;

using Xunit;

namespace Olymp.IntegrationTests.Site;

[Trait("Category", "Integration")]
public class LocalizationTests(SiteFactory factory) : IClassFixture<SiteFactory>
{
    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void RoleAndLockoutErrorsUseTheCurrentUICulture(string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var scope = factory.Services.CreateScope();
            var describer = scope.ServiceProvider.GetRequiredService<IdentityErrorDescriber>();
            var errors = new[]
            {
                describer.InvalidRoleName("Jury"),
                describer.DuplicateRoleName("Jury"),
                describer.UserLockoutNotEnabled(),
                describer.UserAlreadyInRole("Jury"),
                describer.UserNotInRole("Jury")
            };
            string[] expected = culture == "ru"
                ? ["Недопустимое имя роли «Jury».", "Роль с именем «Jury» уже существует.",
                    "Блокировка для этого пользователя не включена.", "Пользователю уже назначена роль «Jury».",
                    "Пользователю не назначена роль «Jury»."]
                : ["Role name 'Jury' is invalid.", "Role name 'Jury' is already taken.",
                    "Lockout is not enabled for this user.", "User already in role 'Jury'.", "User is not in role 'Jury'."];

            Assert.Equal(expected, errors.Select(error => error.Description));
            Assert.Equal(["InvalidRoleName", "DuplicateRoleName", "UserLockoutNotEnabled", "UserAlreadyInRole", "UserNotInRole"],
                errors.Select(error => error.Code));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public async Task RegistrationLoadsTheUnobtrusiveValidationScript()
    {
        using var client = factory.CreateSiteClient();
        using var page = await client.GetAsync("/Identity/Account/Register", TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var script = Assert.Single(document.QuerySelectorAll("script[src]"),
            element => element.GetAttribute("src")!.Contains("jquery.validate.unobtrusive"));

        using var response = await client.GetAsync(script.GetAttribute("src"), TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Contains("unobtrusive", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task PasswordFormsUseTheConfiguredMinimumLength(string culture)
    {
        using var client = await factory.LoginAsync("member");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        var minimum = factory.Services.GetRequiredService<IOptions<IdentityOptions>>().Value.Password.RequiredLength;
        var forms = new[]
        {
            (Path: "/Identity/Account/Register", Field: "Input.Password"),
            (Path: "/Identity/Account/ResetPassword?code=dGVzdA", Field: "Input.Password"),
            (Path: "/Identity/Account/Manage/ChangePassword", Field: "Input.NewPassword")
        };
        foreach (var (path, field) in forms)
        {
            using var page = await client.GetAsync(path, TestContext.Current.CancellationToken);
            page.EnsureSuccessStatusCode();
            using var document = await new HtmlParser().ParseDocumentAsync(
                await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            Assert.Equal(minimum.ToString(), document.QuerySelector($"input[name='{field}']")!.GetAttribute("data-val-length-min"));
        }
    }

    [Theory]
    [InlineData("ru", "Значение «abc» недопустимо для поля «Вуз».")]
    [InlineData("en", "The value 'abc' is not valid for University.")]
    public async Task InvalidNumericInputUsesTheRequestCulture(string culture, string message)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);

        using var response = await SiteFactory.PostFormAsync(client, "/Identity/Account/Register", new()
        {
            ["Input.OrganizationId"] = "abc"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains(message, document.Body!.TextContent);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task DuplicateRegistrationUsesTheRequestCulture(string culture)
    {
        var email = await CreateAccountAsync();
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);

        using var response = await SiteFactory.PostFormAsync(client, "/Identity/Account/Register", new()
        {
            ["Input.PublicName"] = "New user",
            ["Input.Email"] = email,
            ["Input.Password"] = SiteData.Password,
            ["Input.ConfirmPassword"] = SiteData.Password
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var message = culture == "ru" ? $"Логин «{email}» уже занят." : $"User Name '{email}' is already taken.";
        Assert.Contains(message, document.QuerySelector(".validation-summary-errors")!.TextContent);
    }

    [Theory]
    [InlineData("ru", "Недействительный код подтверждения.")]
    [InlineData("en", "Invalid token.")]
    public async Task InvalidPasswordResetTokenUsesTheRequestCulture(string culture, string message)
    {
        var email = await CreateAccountAsync();
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);

        using var response = await SiteFactory.PostFormAsync(client, "/Identity/Account/ResetPassword", new()
        {
            ["Input.Email"] = email,
            ["Input.Password"] = SiteData.Password,
            ["Input.ConfirmPassword"] = SiteData.Password,
            ["Input.Code"] = "invalid"
        }, tokenPage: "/Identity/Account/ResetPassword?code=dGVzdA");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains(message, document.QuerySelector(".validation-summary-errors")!.TextContent);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task AccountErrorResponsesUseTheRequestCulture(string culture)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        var codeMessage = culture == "ru"
            ? "Для сброса пароля необходимо указать код."
            : "A code must be supplied for password reset.";
        var userMessage = culture == "ru"
            ? "Не удалось найти пользователя с ID «-1»."
            : "Unable to load user with ID '-1'.";
        var emailMessage = culture == "ru"
            ? "Не удалось найти пользователя с Email «missing@example.test»."
            : "Unable to load user with email 'missing@example.test'.";
        var cases = new[]
        {
            (Path: "/Identity/Account/ResetPassword", Status: HttpStatusCode.BadRequest, Message: codeMessage),
            (Path: "/Identity/Account/ConfirmEmail?userId=-1&code=dGVzdA", Status: HttpStatusCode.NotFound, Message: userMessage),
            (Path: "/Identity/Account/ConfirmEmailChange?userId=-1&email=missing@example.test&code=dGVzdA", Status: HttpStatusCode.NotFound, Message: userMessage),
            (Path: "/Identity/Account/RegisterConfirmation?email=missing@example.test", Status: HttpStatusCode.NotFound, Message: emailMessage)
        };
        foreach (var (path, status, message) in cases)
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(status, response.StatusCode);
            Assert.Equal(message, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task ProfileValidationExplainsExistingCharacterRestrictions(string culture)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        var invalid = new Dictionary<string, string>
        {
            ["Input.PublicName"] = "bad_name",
            ["Input.FirstName"] = "John",
            ["Input.LastName"] = "Я",
            ["Input.Patronymic"] = "Иван1",
            ["Input.Group"] = "group 1"
        };
        using var response = await SiteFactory.PostFormAsync(client, "/Identity/Account/Register", invalid);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        foreach (var field in invalid.Keys.Where(field => field != "__RequestVerificationToken"))
        {
            var error = document.QuerySelector($"[data-valmsg-for='{field}']")!.TextContent;
            Assert.Contains(culture == "ru" ? "русские" : "Russian", error);
            Assert.DoesNotContain("[А-Я", error);
            Assert.DoesNotContain("regular expression", error);
        }

        // Email and password stay empty, so accepted names cannot create an account.
        var valid = new Dictionary<string, string>
        {
            ["Input.PublicName"] = "New user",
            ["Input.FirstName"] = "Иван",
            ["Input.LastName"] = "Ли",
            ["Input.Patronymic"] = "Иванович",
            ["Input.Group"] = "АБ-12"
        };
        using var accepted = await SiteFactory.PostFormAsync(client, "/Identity/Account/Register", valid);
        accepted.EnsureSuccessStatusCode();
        using var acceptedDocument = await new HtmlParser().ParseDocumentAsync(
            await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        foreach (var field in valid.Keys.Where(field => field != "__RequestVerificationToken"))
            Assert.Empty(acceptedDocument.QuerySelector($"[data-valmsg-for='{field}']")!.TextContent);
    }

    [Theory]
    [InlineData("ru", "Превышение лимита времени на тесте 1", "Превышение лимита реального времени на тесте 1")]
    [InlineData("en", "Time limit exceeded on test 1", "Idleness limit exceeded on test 1")]
    public async Task SubmissionTimeLimitsHaveDistinctDescriptions(string culture, string cpuMessage, string wallMessage)
    {
        using var client = await factory.LoginAsync("team");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var submission = db.Submissions.Where(item => item.Id == factory.Data.Submission.Id);
        var original = await submission.Select(item => item.StatusCode).SingleAsync(TestContext.Current.CancellationToken);
        try
        {
            foreach (var (status, message) in new[] { (Status: -2001, Message: cpuMessage), (Status: -5001, Message: wallMessage) })
            {
                await submission.ExecuteUpdateAsync(update => update.SetProperty(item => item.StatusCode, status), TestContext.Current.CancellationToken);
                using var response = await client.GetAsync($"/Contests/Submits/{factory.Data.Contest.Id}", TestContext.Current.CancellationToken);
                response.EnsureSuccessStatusCode();
                using var document = await new HtmlParser().ParseDocumentAsync(
                    await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

                Assert.Contains(message, document.QuerySelector("#submissions-tab-pane")!.TextContent);
            }
        }
        finally
        {
            await submission.ExecuteUpdateAsync(update => update.SetProperty(item => item.StatusCode, original), TestContext.Current.CancellationToken);
        }
    }

    private async Task<string> CreateAccountAsync()
    {
        var email = $"localization-{Guid.NewGuid():N}@example.test";
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var result = await users.CreateAsync(new OlympUser
        {
            Name = "Existing user",
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            Memberships = [],
            Teams = [],
            Messages = [],
            Submissions = []
        }, SiteData.Password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
        return email;
    }
}

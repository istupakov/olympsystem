using System.Net;

using AngleSharp.Html.Parser;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class EmailConfirmationTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("ru", "Подтверждение регистрации", "Проверьте почту, чтобы подтвердить учётную запись.")]
    [InlineData("en", "Register confirmation", "Please check your email to confirm your account.")]
    public async Task RegistrationConfirmationExplainsNextStepWithoutExposingLink(string culture, string title, string message)
    {
        var user = await factory.AddUserAsync(emailConfirmed: false);
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync($"/Account/RegisterConfirmation?email={Uri.EscapeDataString(user.Email!)}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(title, document.Title);
        Assert.Equal(title, document.QuerySelector("h1")!.TextContent);
        Assert.Equal(message, document.QuerySelector("p[role='alert']")!.TextContent);
        Assert.Null(document.QuerySelector("a[href*='ConfirmEmail?']"));
    }

    [Theory]
    [InlineData("ru", "Повторно отправить письмо подтверждения", "Введите email.", "Отправить повторно", "Письмо подтверждения отправлено. Проверьте почту.")]
    [InlineData("en", "Resend email confirmation", "Enter your email.", "Resend", "Verification email sent. Please check your email.")]
    public async Task ResendLocalizesFormAndReturnsSameMessageForKnownAndUnknownEmails(string culture, string title,
        string prompt, string button, string message)
    {
        var user = await factory.AddUserAsync(emailConfirmed: false);
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var page = await client.GetAsync("/Account/ResendEmailConfirmation", TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(title, document.Title);
        Assert.Equal(title, document.QuerySelector("h1")!.TextContent);
        Assert.Equal(prompt, document.QuerySelector("h2")!.TextContent);
        Assert.Equal("name@example.com", document.QuerySelector("input[name='Input.Email']")!.GetAttribute("placeholder"));
        Assert.Equal("Email", document.QuerySelector("label[for='Input.Email']")!.TextContent.Trim());
        Assert.Equal(button, document.QuerySelector("form:has(input[name='Input.Email']) button[type='submit']")!.TextContent);

        foreach (string email in new[] { user.Email!, $"{Guid.NewGuid():N}@example.test" })
        {
            int lettersBefore = factory.EmailSender.Confirmations.Count;
            using var response = await WebAppFactory.PostFormAsync(client, "/Account/ResendEmailConfirmation", "resend-email-confirmation",
                new() { ["Input.Email"] = email });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var result = await new HtmlParser().ParseDocumentAsync(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            Assert.Equal(message, result.QuerySelector(".alert-success")!.TextContent.Trim());
            Assert.Null(result.QuerySelector(".alert-danger"));
            Assert.Equal(lettersBefore + (email == user.Email ? 1 : 0), factory.EmailSender.Confirmations.Count);
        }
    }

    [Theory]
    [InlineData("ru", "", "Поле «Email» обязательно для заполнения.")]
    [InlineData("en", "", "The Email field is required.")]
    [InlineData("ru", "invalid-email", "Поле «Email» должно содержать корректный адрес электронной почты.")]
    [InlineData("en", "invalid-email", "The Email field is not a valid e-mail address.")]
    public async Task ResendValidationIsLocalizedAndDoesNotSendMail(string culture, string email, string message)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        int lettersBefore = factory.EmailSender.Confirmations.Count;
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/ResendEmailConfirmation", "resend-email-confirmation",
            new() { ["Input.Email"] = email });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(message, Assert.Single(document.QuerySelectorAll(".validation-message")).TextContent);
        Assert.Equal(lettersBefore, factory.EmailSender.Confirmations.Count);
    }

    [Theory]
    [InlineData("ru", "Недействительная ссылка подтверждения смены email.")]
    [InlineData("en", "Invalid email change confirmation link.")]
    public async Task IncompleteEmailChangeLinkShowsLocalizedErrorOnLogin(string culture, string message)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/Account/ConfirmEmailChange", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Login", new Uri(client.BaseAddress!, response.Headers.Location!).AbsolutePath);
        using var login = await client.GetAsync(response.Headers.Location, TestContext.Current.CancellationToken);
        login.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(message, document.QuerySelector(".alert-danger")!.TextContent.Trim());
    }

    [Theory]
    [InlineData("ru", "Пользователь не найден.")]
    [InlineData("en", "User not found.")]
    public async Task EmailChangeForMissingUserShowsLocalizedError(string culture, string message)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/Account/ConfirmEmailChange?userId=-1&email=new%40example.test&code=invalid",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(message, document.QuerySelector(".alert-danger")!.TextContent.Trim());
    }
}

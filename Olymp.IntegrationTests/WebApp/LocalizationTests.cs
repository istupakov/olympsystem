using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Localization;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class LocalizationTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("ru", "Регистрация", "Создайте новую учётную запись.", "Публичное имя", "Пароль", "Подтверждение пароля")]
    [InlineData("en", "Register", "Create a new account.", "Public name", "Password", "Confirm password")]
    public async Task RegistrationPageLocalizesTextAndFieldNames(string culture, string title, string prompt,
        string publicName, string password, string confirmPassword)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/Account/Register", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.Equal(title, document.Title);
        Assert.Equal(title, document.QuerySelector("h1")!.TextContent);
        Assert.Equal(prompt, document.QuerySelector("h2")!.TextContent);
        var form = document.QuerySelector("form[method='post']:has(input[name='Input.PublicName'])")!;
        Assert.Equal(title, form.QuerySelector("button[type='submit']")!.TextContent);
        foreach (var (field, label) in new[] { ("PublicName", publicName), ("Email", "Email"),
            ("Password", password), ("ConfirmPassword", confirmPassword) })
        {
            Assert.Equal(label, form.QuerySelector($"label[for='Input.{field}']")!.TextContent.Trim());
            if (field != "Email")
            {
                string placeholder = field is "Password" or "ConfirmPassword" ? (culture == "ru" ? "пароль" : "password") : label;
                Assert.Equal(placeholder, form.QuerySelector($"input[name='Input.{field}']")!.GetAttribute("placeholder"));
            }
        }
    }

    [Theory]
    [InlineData("ru", "Поле «Публичное имя» обязательно для заполнения.", "Поле «Email» обязательно для заполнения.", "Поле «Пароль» обязательно для заполнения.")]
    [InlineData("en", "The Public name field is required.", "The Email field is required.", "The Password field is required.")]
    public async Task RegistrationRequiredFieldsUseLocalizedNames(string culture, string publicNameError, string emailError, string passwordError)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Register", "register", new());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var messages = document.QuerySelectorAll(".validation-message").Select(element => element.TextContent).ToArray();
        Assert.Equal(4, messages.Length);
        Assert.Contains(publicNameError, messages);
        Assert.Contains(emailError, messages);
        Assert.Contains(passwordError, messages);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task RegistrationValidationLocalizesRulesAndRejectsInvalidInput(string culture)
    {
        bool russian = culture == "ru";
        var cases = new[]
        {
            (Field: "PublicName", Value: "bad_name", Message: russian
                ? "Поле «Публичное имя» может содержать только русские или латинские буквы, цифры, пробелы или дефисы."
                : "The Public name field may contain only Russian or Latin letters, digits, spaces or hyphens."),
            (Field: "PublicName", Value: new string('A', 33), Message: russian
                ? "Поле «Публичное имя» должно содержать не более 32 символов."
                : "The field Public name must be a string with a maximum length of 32."),
            (Field: "Email", Value: "not-an-email", Message: russian
                ? "Поле «Email» должно содержать корректный адрес электронной почты."
                : "The Email field is not a valid e-mail address."),
            (Field: "Password", Value: "Short1", Message: russian
                ? "Длина поля «Пароль» должна быть от 8 до 100 символов."
                : "The Password must be at least 8 and at max 100 characters long."),
            (Field: "Password", Value: new string('A', 101), Message: russian
                ? "Длина поля «Пароль» должна быть от 8 до 100 символов."
                : "The Password must be at least 8 and at max 100 characters long."),
            (Field: "ConfirmPassword", Value: "DifferentPassword456", Message: russian
                ? "Пароль и подтверждение пароля не совпадают."
                : "The password and confirmation password do not match.")
        };
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        int lettersBefore = factory.EmailSender.Confirmations.Count;
        foreach (var (field, value, message) in cases)
        {
            var fields = new Dictionary<string, string>
            {
                ["Input.PublicName"] = "New user",
                ["Input.Email"] = $"{Guid.NewGuid():N}@example.test",
                ["Input.Password"] = WebAppFactory.Password,
                ["Input.AcceptRules"] = "true",
                ["Input.ConfirmPassword"] = WebAppFactory.Password
            };
            fields[$"Input.{field}"] = value;
            if (field == "Password")
                fields["Input.ConfirmPassword"] = value;
            using var response = await WebAppFactory.PostFormAsync(client, "/Account/Register", "register", fields);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = await new HtmlParser().ParseDocumentAsync(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            Assert.Equal(message, Assert.Single(document.QuerySelectorAll(".validation-message")).TextContent);
        }
        Assert.Equal(lettersBefore, factory.EmailSender.Confirmations.Count);
    }

    [Theory]
    [InlineData("ru", "Вход", "Email или логин", "Пароль", "Запомнить меня?", "Забыли пароль?")]
    [InlineData("en", "Log in", "Email or username", "Password", "Remember me?", "Forgot your password?")]
    [InlineData("fr", "Log in", "Email or username", "Password", "Remember me?", "Forgot your password?")]
    public async Task LoginPageLocalizesTextAndFieldNames(string culture, string title, string login, string password,
        string rememberMe, string forgotPassword)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.Equal(title, document.Title);
        Assert.Equal(title, document.QuerySelector("h1")!.TextContent);
        Assert.Equal(login, document.QuerySelector("label[for='Input.Login']")!.TextContent.Trim());
        Assert.Equal(password, document.QuerySelector("label[for='Input.Password']")!.TextContent.Trim());
        Assert.Contains(rememberMe, document.QuerySelector(".checkbox label")!.TextContent);
        Assert.Equal(forgotPassword, document.QuerySelector("a[href='account/forgotpassword']")!.TextContent);
        Assert.Equal(login, document.QuerySelector("input[name='Input.Login']")!.GetAttribute("placeholder"));
        Assert.Equal(culture == "ru" ? "пароль" : "password",
            document.QuerySelector("input[name='Input.Password']")!.GetAttribute("placeholder"));
    }

    [Theory]
    [InlineData("ru", "Поле «Email или логин» обязательно для заполнения.", "Поле «Пароль» обязательно для заполнения.")]
    [InlineData("en", "The Email or username field is required.", "The Password field is required.")]
    public async Task LoginValidationLocalizesMessagesAndFieldNames(string culture, string loginError, string passwordError)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var messages = document.QuerySelectorAll(".validation-message").Select(element => element.TextContent).ToArray();
        Assert.Contains(loginError, messages);
        Assert.Contains(passwordError, messages);
    }

    [Theory]
    [InlineData("ru", "Не удалось войти.")]
    [InlineData("en", "Invalid login attempt.")]
    public async Task LoginFailureIsLocalizedAndKeepsErrorStyle(string culture, string message)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new()
        {
            ["Input.Login"] = $"missing_{Guid.NewGuid():N}",
            ["Input.Password"] = WebAppFactory.Password
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(message, document.QuerySelector(".alert-danger")!.TextContent.Trim());
        Assert.Null(document.QuerySelector(".alert-success"));
    }

    [Theory]
    [InlineData(null, "en")]
    [InlineData("ru", "ru")]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("ru-RU", "ru")]
    [InlineData("fr", "en")]
    public async Task PageUsesBrowserLanguageOrEnglishByDefault(string? browserLanguage, string expected)
    {
        using var client = factory.CreateSiteClient();
        if (browserLanguage is not null)
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(browserLanguage);

        await AssertPageLanguageAsync(client, "/Account/Login", expected);
    }

    [Theory]
    [InlineData("en", "ru")]
    [InlineData("ru", "en")]
    public async Task SelectorPersistsLanguageAndOverridesBrowserPreference(string selected, string browserLanguage)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(browserLanguage);
        const string path = "/Account/Login?ReturnUrl=%2Ftest%2Fauth";
        using var page = await client.GetAsync(path, TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var form = Assert.Single(document.QuerySelectorAll("form[action='language']"));
        Assert.Equal("false", form.GetAttribute("data-enhance"));
        Assert.Equal(["ru", "en"], form.QuerySelectorAll("button[name='culture']").Select(button => button.GetAttribute("value")));
        Assert.Equal(path, form.QuerySelector("input[name='returnUrl']")!.GetAttribute("value"));
        using var response = await SelectLanguageAsync(client, selected, path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(path, response.Headers.Location!.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(CookieRequestCultureProvider.DefaultCookieName + "=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        await AssertPageLanguageAsync(client, path, selected);
        await AssertPageLanguageAsync(client, "/Account/Register", selected);
        // Explicit selection is kept even when a link contains another culture.
        await AssertPageLanguageAsync(client, $"/Account/Login?culture={browserLanguage}&ui-culture={browserLanguage}", selected);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("invalid")]
    public async Task UnsupportedLanguageIsRejectedWithoutSettingCookie(string culture)
    {
        using var client = factory.CreateSiteClient();
        using var response = await SelectLanguageAsync(client, culture, "/Account/Login");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await AssertPageLanguageAsync(client, "/Account/Login", "en");
    }

    [Theory]
    [InlineData("https://other.example.test/")]
    [InlineData("//other.example.test/")]
    public async Task LanguageSelectionDoesNotRedirectToAnotherSite(string returnUrl)
    {
        using var client = factory.CreateSiteClient();
        using var response = await SelectLanguageAsync(client, "en", returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
        await AssertPageLanguageAsync(client, "/Account/Login", "en");
    }

    [Fact]
    public async Task CrossSiteLanguageSelectionIsRejected()
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        client.DefaultRequestHeaders.Add("Origin", "https://other.example.test");
        using var response = await SelectLanguageAsync(client, "en", "/Account/Login");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private static async Task<HttpResponseMessage> SelectLanguageAsync(HttpClient client, string culture, string returnUrl)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["culture"] = culture,
            ["returnUrl"] = returnUrl
        });
        return await client.PostAsync("/Language", content, TestContext.Current.CancellationToken);
    }

    private static async Task AssertPageLanguageAsync(HttpClient client, string path, string expected)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        Assert.Equal([expected], response.Content.Headers.ContentLanguage);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(expected, document.DocumentElement.GetAttribute("lang"));
        Assert.NotNull(document.QuerySelector($"form[action='language'] button[value='{expected}'][disabled]"));
    }
}

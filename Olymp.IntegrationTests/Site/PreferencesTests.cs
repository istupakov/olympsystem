using System.Net;

using AngleSharp.Html.Parser;

using Xunit;

namespace Olymp.IntegrationTests.Site;

[Trait("Category", "Integration")]
public class PreferencesTests(SiteFactory factory) : IClassFixture<SiteFactory>
{
    [Theory]
    [InlineData("ru", "RU")]
    [InlineData("en", "EN")]
    [InlineData("de", "RU")]
    public async Task GuestsSeeBrowserLanguageAndCanChoosePreferences(string browserLanguage, string label)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(browserLanguage);

        using var response = await client.GetAsync("/Rules?n=2", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(label, document.QuerySelector("#language-toggle")!.TextContent.Trim());
        Assert.NotNull(document.QuerySelector("#theme-toggle"));
        var form = document.QuerySelector("form[action='/language']")!;
        Assert.Equal("/Rules?n=2", form.QuerySelector("input[name='ReturnUrl']")!.GetAttribute("value"));
        Assert.Equal("", form.QuerySelector("button[aria-pressed='true']")!.GetAttribute("value"));
    }

    [Theory]
    [InlineData("ru", "en")]
    [InlineData("en", "ru")]
    public async Task GuestChoiceOverridesBrowserAndPreservesCurrentPage(string language, string browserLanguage)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(browserLanguage);

        using var response = await ChangeLanguageAsync(client, language, "/Rules?n=2&filter=a%20b");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Rules?n=2&filter=a%20b", response.Headers.Location!.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith(".AspNetCore.Culture="));
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        using var page = await client.GetAsync(response.Headers.Location, TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(language, document.DocumentElement.GetAttribute("lang"));
        Assert.Equal(language, document.QuerySelector("button[name='Language'][aria-pressed='true']")!.GetAttribute("value"));
    }

    [Fact]
    public async Task BrowserSettingClearsExplicitChoice()
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru");
        using var choice = await ChangeLanguageAsync(client, "en");
        Assert.Equal(HttpStatusCode.Redirect, choice.StatusCode);

        using var response = await ChangeLanguageAsync(client, "");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var page = await client.GetAsync(response.Headers.Location, TestContext.Current.CancellationToken);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("ru", document.DocumentElement.GetAttribute("lang"));
        Assert.Equal("", document.QuerySelector("button[name='Language'][aria-pressed='true']")!.GetAttribute("value"));
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en-US")]
    [InlineData("not-a-culture")]
    public async Task UnsupportedLanguageIsRejected(string language)
    {
        using var client = factory.CreateSiteClient();

        using var response = await ChangeLanguageAsync(client, language);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(cookie => cookie.StartsWith(".AspNetCore.Culture=")));
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("//example.com/")]
    [InlineData("/\\example.com/")]
    [InlineData("")]
    public async Task UnsafeReturnUrlFallsBackToHome(string returnUrl)
    {
        using var client = factory.CreateSiteClient();

        using var response = await ChangeLanguageAsync(client, "en", returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ChoiceRemovesQueryCultureOverridesButKeepsOtherParameters()
    {
        using var client = factory.CreateSiteClient();

        using var response = await ChangeLanguageAsync(client, "en", "/Rules?culture=ru&ui-culture=ru&n=2");

        Assert.Equal("/Rules?n=2", response.Headers.Location!.OriginalString);
        using var page = await client.GetAsync(response.Headers.Location, TestContext.Current.CancellationToken);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("en", document.DocumentElement.GetAttribute("lang"));
    }

    [Fact]
    public async Task ChangingLanguageRequiresAntiforgeryToken()
    {
        using var client = factory.CreateSiteClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["Language"] = "en" });

        using var response = await client.PostAsync("/language", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CompetitorCanChangeLanguageWithoutAccessToAccountSettings()
    {
        using var client = await factory.LoginAsync("team");

        using var response = await ChangeLanguageAsync(client, "en");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Rules", response.Headers.Location!.OriginalString);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(".AspNetCore.Culture="));
    }

    [Fact]
    public async Task ProfileUsesSharedLanguageControlAndHasNoLanguagePage()
    {
        using var client = await factory.LoginAsync("member");

        using var response = await client.GetAsync("/Identity/Account/Manage", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.NotNull(document.QuerySelector("#language-toggle"));
        Assert.Empty(document.QuerySelectorAll("a[href='/Identity/Account/Manage/Language']"));
        using var removedPage = await client.GetAsync("/Identity/Account/Manage/Language", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, removedPage.StatusCode);
    }

    private static Task<HttpResponseMessage> ChangeLanguageAsync(HttpClient client, string language, string returnUrl = "/Rules") =>
        SiteFactory.PostFormAsync(client, "/language", new()
        {
            ["Language"] = language,
            ["ReturnUrl"] = returnUrl
        }, tokenPage: "/Rules");
}

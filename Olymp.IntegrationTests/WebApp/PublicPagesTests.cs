using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class PublicPagesTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("/olympiad/standing/3066", "/contests/3066/standing")]
    [InlineData("/olympiad/standing/3066?official=true", "/contests/3066/standing?official=true")]
    [InlineData("/contests/standing/3066", "/contests/3066/standing")]
    [InlineData("/contests/standing/3066/?official=true", "/contests/3066/standing?official=true")]
    public async Task LegacyStandingsRedirectPermanentlyToCurrentRoute(string path, string expectedPath)
    {
        using var client = factory.CreateSiteClient();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        var location = new Uri(client.BaseAddress!, response.Headers.Location!);
        Assert.Equal(expectedPath, location.PathAndQuery);
    }

    [Theory]
    [InlineData("/contests/standing/3066/extra")]
    [InlineData("/extra/olympiad/standing/3066")]
    public async Task LegacyStandingsRedirectOnlyMatchesWholePath(string path)
    {
        using var client = factory.CreateSiteClient();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task WwwRedirectPreservesPathQueryAndPort()
    {
        using var client = factory.CreateSiteClient();
        using var response = await client.GetAsync("https://www.example.test:5001/contests?year=2026", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal("https://example.test:5001/contests?year=2026", response.Headers.Location!.AbsoluteUri);
    }

    [Theory]
    [InlineData("ru", "Страница не найдена", "К сожалению, запрашиваемая страница не существует.")]
    [InlineData("en", "Not Found", "Sorry, the content you are looking for does not exist.")]
    public async Task UnknownPageReturnsLocalizedNotFound(string culture, string title, string message)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/missing-public-page", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(title, document.Title);
        Assert.Equal(title, document.QuerySelector("main h1")!.TextContent);
        Assert.Equal(message, document.QuerySelector("main p")!.TextContent);
        Assert.Equal(culture, document.DocumentElement.GetAttribute("lang"));
    }

    [Theory]
    [InlineData("ru", "Новости", "Соревнования", "Контакты", "Вход", "Регистрация")]
    [InlineData("en", "News", "Contests", "Contacts", "Log in", "Register")]
    public async Task PublicHeaderAndFooterAreLocalizedAndWorkWithoutInteractiveMenu(string culture, string news,
        string contests, string contacts, string login, string register)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = await new HtmlParser().ParseDocumentAsync(html, TestContext.Current.CancellationToken);
        Assert.Equal(news, document.Title);
        Assert.Equal(news, document.QuerySelector("main h1")!.TextContent);
        Assert.Equal("OlympSystem", document.QuerySelector("header .navbar-brand")!.TextContent.Trim());
        Assert.Equal([news, contests, culture == "ru" ? "Правила" : "Rules"], document.QuerySelectorAll("header ul.nav a").Select(link => link.TextContent));
        Assert.Equal(news, Assert.Single(document.QuerySelectorAll("header ul.nav a.active")).TextContent);
        Assert.Equal("contests", document.QuerySelector("header ul.nav a[href='contests']")!.GetAttribute("href"));
        Assert.NotNull(document.QuerySelector("header ul.nav a[href='rules']"));
        Assert.Equal(login, document.QuerySelector("header a[href='account/login']")!.TextContent);
        Assert.Equal(register, document.QuerySelector("header a[href='account/register']")!.TextContent);
        Assert.Null(document.QuerySelector("header a[href='account/manage']"));
        Assert.Equal(contacts, document.QuerySelector("footer a[href='contacts']")!.TextContent);
        Assert.Equal("GitHub", document.QuerySelector("footer a[href='https://github.com/istupakov/olympsystem']")!.TextContent.Trim());
        var footer = document.QuerySelector("footer")!;
        Assert.Equal(culture == "ru" ? "НГТУ" : "NSTU", footer.QuerySelector(culture == "ru" ? "a[href='https://nstu.ru']" : "a[href='https://en.nstu.ru']")!.TextContent);
        Assert.NotNull(footer.QuerySelector("a[href='https://ami.nstu.ru']"));
        Assert.Equal(culture == "ru" ? "Новости и обсуждения в Telegram" : "News and discussions on Telegram", footer.QuerySelector("a[href='https://t.me/olymp_nstu']")!.TextContent.Trim());
        Assert.NotNull(footer.QuerySelector("a[href='mailto:olymp@ami.nstu.ru']"));
        Assert.Null(document.QuerySelector("header a[href='contacts']"));
        Assert.Null(document.QuerySelector(".sidebar, nav-menu, .navbar-toggler"));
        Assert.Empty(document.QuerySelectorAll("header a[href='counter'], header a[href='weather'], header a[href='auth']"));
        Assert.DoesNotContain("NavMenu.razor.js", html);
        Assert.DoesNotContain("<!--Blazor:", html);
        var selectedLanguage = document.QuerySelector($"header form[action='language'] button[value='{culture}']")!;
        Assert.True(selectedLanguage.HasAttribute("disabled"));
        Assert.Equal(culture.ToUpperInvariant(), selectedLanguage.TextContent);
        Assert.Equal(["RU", "EN"], document.QuerySelectorAll("header form[action='language'] button").Select(button => button.TextContent));
        Assert.NotNull(document.QuerySelector("header button[data-theme-toggle]"));
        var themeToggle = document.QuerySelector("header button[data-theme-toggle]")!;
        Assert.Equal(culture == "ru" ? "Тема: Системная. Следующая: Светлая." : "Theme: System. Next: Light.", themeToggle.GetAttribute("aria-label"));
        Assert.NotNull(document.QuerySelector("head script[src*='theme']"));
        Assert.Null(document.QuerySelector("script[src*='bootstrap']"));
        Assert.Empty(document.QuerySelectorAll("header [data-bs-toggle]"));

        string logoUrl = document.QuerySelector("header img")!.GetAttribute("src")!;
        using var logo = await client.GetAsync(logoUrl, TestContext.Current.CancellationToken);
        logo.EnsureSuccessStatusCode();
        Assert.Equal("image/svg+xml", logo.Content.Headers.ContentType!.MediaType);
        Assert.Contains("<svg", await logo.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task HomeRendersOnlyFiveLatestNewsInFullAndKeepsExistingLinks(string culture)
    {
        const string fullTextEnd = "End of full announcement.";
        const string registrationLink = "../contests/registration/3066";
        const string resultLink = "./../olympiad/standing/3065?official=true";
        const string sponsorImage = "sponsors/T-Technology_logo_rus_black.svg";
        string body = $"<p><strong>Registration details</strong> {string.Concat(Enumerable.Repeat("Full text. ", 300))}{fullTextEnd}</p>"
            + $"<a href='{registrationLink}'>Register</a><a href='{resultLink}'>Results</a>"
            + $"<img src='{sponsorImage}' alt='Sponsor'>";
        var published = new DateTimeOffset(2026, 5, 30, 14, 0, 0, TimeSpan.FromHours(7));
        var items = Enumerable.Range(0, 7).Select(index => new News
        {
            Title = index == 6 ? "Latest <em>news</em>" : $"News {index}",
            Text = index == 6 ? body : $"<p>Content {index}</p>",
            PublicationDate = published.AddDays(index)
        }).ToArray();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            await db.News.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            db.News.AddRange(items);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var articles = document.QuerySelectorAll("main article");
        Assert.Equal(items.Reverse().Take(5).Select(item => item.Title), articles.Select(article => article.QuerySelector("h2")!.TextContent));
        var latest = articles[0];
        Assert.Null(latest.QuerySelector("h2 em"));
        Assert.Equal("Registration details", latest.QuerySelector(".card-body strong")!.TextContent);
        Assert.Contains(fullTextEnd, latest.QuerySelector(".card-body")!.TextContent);
        Assert.Equal([registrationLink, resultLink], latest.QuerySelectorAll(".card-body a").Select(link => link.GetAttribute("href")));
        Assert.Equal(sponsorImage, latest.QuerySelector(".card-body img")!.GetAttribute("src"));
        var time = latest.QuerySelector("time")!;
        Assert.Equal(items[6].PublicationDate, DateTimeOffset.Parse(time.GetAttribute("datetime")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(culture == "ru" ? "пятница, 5 июня 2026 г." : "Friday, June 5, 2026",
            time.TextContent.Trim().Replace('\u202f', ' ').Replace('\u00a0', ' '));
        Assert.Null(document.QuerySelector(".pagination"));
        using var verification = factory.Services.CreateScope();
        Assert.Equal(7, await verification.ServiceProvider.GetRequiredService<OlympContext>().News.CountAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("ru", "Новости пока не опубликованы.")]
    [InlineData("en", "No news has been published yet.")]
    public async Task EmptyHomeIsPublicAndShowsLocalizedMessage(string culture, string message)
    {
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<OlympContext>().News.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Empty(document.QuerySelectorAll("main article"));
        Assert.Equal(message, document.QuerySelector("main p")!.TextContent);
    }

    [Theory]
    [InlineData("ru", "Контактная информация", "Рояк Михаил Эммануилович", "Ступаков Илья Михайлович")]
    [InlineData("en", "Contact Info", "Royak Mikhail Emmanuilovich", "Stupakov Ilya Mikhailovich")]
    public async Task ContactsArePublicAndPreserveExistingContactDetails(string culture, string title, string director, string developer)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/Contacts", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(title, document.Title);
        Assert.Equal(title, document.QuerySelector("main h1")!.TextContent);
        Assert.Equal([director, developer], document.QuerySelectorAll("main h2").Select(heading => heading.TextContent));
        Assert.NotNull(document.QuerySelector("main a[href='tel:+73833462776']"));
        Assert.NotNull(document.QuerySelector("main a[href='mailto:royak@fpm.ami.nstu.ru']"));
        Assert.NotNull(document.QuerySelector("main a[href='mailto:mikeroyak@gmail.com']"));
        Assert.NotNull(document.QuerySelector("main a[href='mailto:istupakov@gmail.com']"));
        var cards = document.QuerySelectorAll("main article");
        Assert.Equal(2, cards.Length);
        var organizer = cards[0];
        Assert.Equal(culture == "ru" ? "Директор соревнований" : "Contest Director", organizer.QuerySelector(".card-header")!.TextContent);
        var project = cards[1];
        Assert.NotNull(project.QuerySelector("a[href='mailto:istupakov@gmail.com']"));
        Assert.Equal(culture == "ru" ? "к.т.н., доцент каф. ПМт НГТУ" : "PhD, Associate Professor, Department of Applied Mathematics, NSTU", project.QuerySelector(".card-subtitle")!.TextContent);
    }

    [Theory]
    [InlineData("ru", "Профиль", "Выход")]
    [InlineData("en", "Profile", "Logout")]
    public async Task HeaderShowsProfileAndLogoutForSignedInUser(string culture, string profile, string logout)
    {
        var user = await factory.AddUserAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var page = await client.GetAsync("/Contacts", TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(user.Name, document.QuerySelector("header a[href='account/manage']")!.TextContent);
        Assert.Equal(profile, document.QuerySelector("header a[href='account/manage']")!.GetAttribute("title"));
        Assert.Null(document.QuerySelector("header a[href='account/login'], header a[href='account/register']"));
        var form = document.QuerySelector("header form[action='account/logout']")!;
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal(logout, form.QuerySelector("button")!.TextContent);
        Assert.Equal("Contacts", form.QuerySelector("input[name='ReturnUrl']")!.GetAttribute("value"));
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["ReturnUrl"] = "Contacts" });
        using var response = await client.PostAsync("/Account/Logout", content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Contacts", new Uri(client.BaseAddress!, response.Headers.Location!).AbsolutePath);
        using var protectedPage = await client.GetAsync("/Account/Manage", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, protectedPage.StatusCode);
        Assert.Equal("/Account/Login", new Uri(client.BaseAddress!, protectedPage.Headers.Location!).AbsolutePath);
    }
}

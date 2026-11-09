using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class ContestListTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("ru", "Соревнования", "Текущие и предстоящие", "Прошедшие соревнования")]
    [InlineData("en", "Contests", "Current and upcoming", "Past contests")]
    public async Task ListGroupsContestsAndShowsDescriptionsOnStandings(string culture, string title, string upcoming, string archive)
    {
        var now = DateTimeOffset.UtcNow;
        var current = CreateContest("Current", now.AddHours(-1), now.AddHours(1));
        var future = CreateContest("Future", now.AddDays(10), now.AddDays(11));
        future.Description = "<p>Registration announcement</p>";
        var recent = CreateContest("Recent <em>contest</em>", new(2025, 6, 1, 10, 0, 0, TimeSpan.FromHours(7)));
        recent.Description = "<p><strong>Results</strong> <a href='../legacy/results?official=true'>Original link</a></p>";
        recent.IsOpen = true;
        var earlier = CreateContest("Earlier", new(2025, 1, 1, 10, 0, 0, TimeSpan.FromHours(7)));
        var oldest = CreateContest("Oldest", new(2024, 6, 1, 10, 0, 0, TimeSpan.FromHours(7)));
        var hidden = CreateContest("Hidden", now.AddDays(1));
        hidden.IsHidden = true;
        await SeedAsync(oldest, earlier, recent, future, current, hidden);

        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/contests", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = await new HtmlParser().ParseDocumentAsync(html, TestContext.Current.CancellationToken);
        Assert.Equal(title, document.Title);
        Assert.Equal(title, document.QuerySelector("main h1")!.TextContent);
        Assert.Equal(title, document.QuerySelector("header ul.nav a.active")!.TextContent);
        Assert.Equal([upcoming, archive], document.QuerySelectorAll("main section > h2").Select(heading => heading.TextContent));
        Assert.Equal([current.Name, future.Name, recent.Name, earlier.Name, oldest.Name],
            document.QuerySelectorAll("main article h3").Select(heading => heading.TextContent));
        var items = document.QuerySelectorAll("main article");
        Assert.StartsWith(culture == "ru" ? "До конца " : "Ends in ", items[0].QuerySelector("p.small")!.TextContent);
        Assert.StartsWith(culture == "ru" ? "До начала " : "Starts in ", items[1].QuerySelector("p.small")!.TextContent);
        Assert.All(items.Skip(2), item => Assert.Null(item.QuerySelector("p.small")));
        Assert.Null(document.QuerySelector("main article h3 em"));
        Assert.Equal(["contests-2025", "contests-2024"], document.QuerySelectorAll("main details[name='contest-years']").Select(group => group.Id));
        Assert.True(document.QuerySelector("#contests-2025")!.HasAttribute("open"));
        Assert.False(document.QuerySelector("#contests-2024")!.HasAttribute("open"));
        Assert.Null(document.QuerySelector("main article strong"));
        Assert.Contains("Registration announcement", document.QuerySelector("main")!.TextContent);
        Assert.Null(document.QuerySelector("main a[href='../legacy/results?official=true']"));
        using var standings = await client.GetAsync($"/contests/{recent.Id}/standing", TestContext.Current.CancellationToken);
        standings.EnsureSuccessStatusCode();
        using var results = await new HtmlParser().ParseDocumentAsync(
            await standings.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal("Results", results.QuerySelector(".contest-description strong")!.TextContent);
        Assert.NotNull(results.QuerySelector(".contest-description a[href='../legacy/results?official=true']"));
        Assert.DoesNotContain("<!--Blazor:", html);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(6, await scope.ServiceProvider.GetRequiredService<OlympContext>().Contests.CountAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("ru", "Участники", "Регистрация", "Результаты", "Скачать условия (PDF)", "Скачать тесты (ZIP)")]
    [InlineData("en", "Participants", "Registration", "Standings", "Download statements (PDF)", "Download tests (ZIP)")]
    public async Task UnavailableActionsFollowContestVisibilityRules(string culture, string participants, string registration,
        string standings, string statements, string tests)
    {
        var now = DateTimeOffset.UtcNow;
        var future = CreateContest("Future", now.AddDays(1));
        future.ProblemPdf = new Blob { Data = [1] };
        future.TestZip = new Blob { Data = [2] };
        var past = CreateContest("Past", now.AddDays(-1));
        past.IsOpen = true;
        past.ProblemPdf = new Blob { Data = [1] };
        past.TestZip = new Blob { Data = [2] };
        await SeedAsync(future, past);
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/contests", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var articles = document.QuerySelectorAll("main article");
        string rules = culture == "ru" ? "Правила" : "Rules";
        Assert.Equal([participants, rules, registration], articles[0].QuerySelectorAll("li").Select(action => action.TextContent.Trim()));
        Assert.NotNull(articles[0].QuerySelector($"a[href='contests/{future.Id}/competitors']"));
        var pastActions = articles[1].QuerySelectorAll("li");
        Assert.Single(pastActions, action => action.TextContent.StartsWith(standings, StringComparison.Ordinal));
        Assert.Equal(6, pastActions.Length);
        Assert.Contains(statements, pastActions.Select(action => action.TextContent));
        Assert.Contains(tests, pastActions.Select(action => action.TextContent));
        Assert.DoesNotContain(registration, pastActions.Select(action => action.TextContent));
        Assert.Equal([
            $"contests/{future.Id}/competitors",
            $"contests/{future.Id}/rules",
            $"contests/{future.Id}/registration",
            $"contests/{past.Id}/standing",
            $"contests/{past.Id}/problems",
            $"contests/{past.Id}/competitors",
            $"contests/{past.Id}/rules",
            $"contests/{past.Id}/downloadproblems",
            $"contests/{past.Id}/downloadtests"
        ], document.QuerySelectorAll("main a").Select(link => link.GetAttribute("href")));
    }

    [Theory]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Jury)]
    public async Task PublicListIsTheSameForGuestsAndStaff(string role)
    {
        var hidden = CreateContest("Hidden contest", DateTimeOffset.UtcNow.AddDays(1));
        hidden.IsHidden = true;
        var visible = CreateContest("Visible contest", DateTimeOffset.UtcNow.AddDays(2));
        visible.ProblemPdf = new Blob { Data = [1] };
        visible.TestZip = new Blob { Data = [2] };
        await SeedAsync(hidden, visible);
        using var guest = factory.CreateSiteClient();
        using var guestResponse = await guest.GetAsync("/contests", TestContext.Current.CancellationToken);
        guestResponse.EnsureSuccessStatusCode();
        using var guestDocument = await new HtmlParser().ParseDocumentAsync(
            await guestResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var user = await factory.AddUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<int>>>();
            if (!await roles.RoleExistsAsync(role))
                Assert.True((await roles.CreateAsync(new IdentityRole<int>(role))).Succeeded);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var stored = await users.FindByIdAsync(user.Id.ToString());
            Assert.True((await users.AddToRoleAsync(stored!, role)).Succeeded);
        }
        using var client = await factory.LoginAsync(user.UserName!);
        using var response = await client.GetAsync("/contests", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(visible.Name, Assert.Single(document.QuerySelectorAll("main article h3")).TextContent);
        // Each request calculates its own remaining time; compare the published content and navigation.
        foreach (var countdown in guestDocument.QuerySelectorAll("main article > p.small")
            .Concat(document.QuerySelectorAll("main article > p.small")))
            countdown.Remove();
        Assert.Equal(guestDocument.QuerySelector("main")!.InnerHtml, document.QuerySelector("main")!.InnerHtml);
    }

    [Theory]
    [InlineData("ru", "Соревнования пока не опубликованы.")]
    [InlineData("en", "No contests have been published yet.")]
    public async Task EmptyListHasLocalizedMessage(string culture, string message)
    {
        await SeedAsync();
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/contests", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(message, document.QuerySelector("main p")!.TextContent);
        Assert.Empty(document.QuerySelectorAll("main article, main details"));
    }

    private async Task SeedAsync(params Contest[] contests)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await context.Contests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        context.Contests.AddRange(contests);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Contest CreateContest(string name, DateTimeOffset start, DateTimeOffset? end = null) => new()
    {
        Name = name,
        Abbr = Guid.NewGuid().ToString("N"),
        StartTime = start,
        EndTime = end ?? start.AddHours(5),
        Messages = [],
        Problems = [],
        Competitors = []
    };
}

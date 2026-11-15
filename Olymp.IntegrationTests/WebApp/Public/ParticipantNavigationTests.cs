using System.Net;
using System.Security.Claims;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Public;

[Trait("Category", "Integration")]
public class ParticipantNavigationTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("ru", "upcoming")]
    [InlineData("en", "upcoming")]
    [InlineData("ru", "running")]
    [InlineData("en", "running")]
    [InlineData("ru", "ended")]
    [InlineData("en", "ended")]
    public async Task TemporaryAccountHasOwnHeaderAndCanReadOtherPublishedContests(string culture, string state)
    {
        var now = DateTimeOffset.UtcNow;
        var contest = TestEntities.CreateContest($"Own contest {Guid.NewGuid():N}",
            now.AddHours(state == "upcoming" ? 1 : -2), now.AddHours(state == "ended" ? -1 : 2));
        var other = TestEntities.CreateContest($"Other contest {Guid.NewGuid():N}", now.AddHours(-2), now.AddHours(2));
        other.Problems.Add(TestEntities.CreateProblem(other));
        contest.Problems.Add(TestEntities.CreateProblem(contest));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            db.Contests.AddRange(contest, other);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var participant = await AddParticipantAsync(contest);

        using var client = await factory.LoginAsync(participant.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        foreach (string section in state == "upcoming" ? new[] { "rules" } : ["problems", "standing", "rules"])
        {
            using var response = await client.GetAsync($"/contests/{contest.Id}/{section}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = await new HtmlParser().ParseDocumentAsync(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            var navbar = document.QuerySelector(".navbar")!;
            Assert.Equal("OlympSystem", navbar.QuerySelector(".navbar-brand")!.TextContent.Trim());
            Assert.NotNull(navbar.QuerySelector(".navbar-brand img"));
            Assert.Empty(navbar.QuerySelectorAll("ul.nav"));
            Assert.Null(navbar.QuerySelector("a[href='account/manage']"));
            Assert.Equal(participant.Name, navbar.QuerySelector(".navbar-text")!.TextContent);
            Assert.NotNull(navbar.QuerySelector("form[action='account/logout']"));
            Assert.NotNull(navbar.QuerySelector("form[action='language']"));
            Assert.NotNull(navbar.QuerySelector("button[data-theme-toggle]"));
            string homeSection = state == "upcoming" ? "rules" : "problems";
            Assert.Equal($"contests/{contest.Id}/{homeSection}", navbar.QuerySelector(".navbar-brand")!.GetAttribute("href"));
            var banner = document.QuerySelector("header section")!;
            var contestLink = Assert.Single(banner.QuerySelectorAll("a"));
            Assert.Equal(contest.Name, contestLink.TextContent);
            Assert.Equal($"contests/{contest.Id}/{(state == "upcoming" ? "rules" : "standing")}", contestLink.GetAttribute("href"));
            Assert.DoesNotContain(other.Name, banner.TextContent);
            Assert.Equal(contest.Name, document.QuerySelector(".contest-header h1")!.TextContent);
            var menu = document.QuerySelector(".contest-header nav")!;
            Assert.Equal(state != "upcoming", menu.QuerySelector($"a[href='contests/{contest.Id}/submissions']") is not null);
            Assert.Equal(state != "upcoming", menu.QuerySelector($"a[href='contests/{contest.Id}/problems']") is not null);
            Assert.Equal(state != "upcoming", menu.QuerySelector($"a[href='contests/{contest.Id}/standing?highlight=true&official=true']") is not null);
            Assert.Equal($"contests/{contest.Id}/{section}" + (section == "standing" ? "?highlight=true&official=true" : ""), menu.QuerySelector("a[aria-current='page']")!.GetAttribute("href"));
            Assert.Equal(state == "running", menu.QuerySelector($"a[href='contests/{contest.Id}/questions']") is not null);
            Assert.Equal(state == "upcoming" ? 2 : state == "running" ? 6 : 5, menu.QuerySelectorAll("a").Length);
            if (section == "rules")
                Assert.DoesNotContain(culture == "ru" ? "Вход для участия" : "Signing In to Participate", document.QuerySelector("main")!.TextContent);
        }

        using var history = await client.GetAsync("/submissions", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var historyDocument = await new HtmlParser().ParseDocumentAsync(
            await history.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(culture == "ru" ? "Решения" : "Submissions", historyDocument.Title);
        Assert.Null(historyDocument.QuerySelector("main .contest-header"));
        Assert.Equal(historyDocument.Title, historyDocument.QuerySelector("main h1")!.TextContent);
        Assert.Null(historyDocument.QuerySelector("#submissions .nav"));
        foreach (string section in new[] { "problems", "rules", "standing", "competitors" })
        {
            using var foreign = await client.GetAsync($"/contests/{other.Id}/{section}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, foreign.StatusCode);
        }
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "/")]
    [InlineData(true, "/")]
    [InlineData(false, "/submissions")]
    [InlineData(true, "/submissions")]
    public async Task TemporaryLoginDefaultsToContestRulesAndPreservesExplicitReturnUrl(bool started, string? returnUrl)
    {
        var now = DateTimeOffset.UtcNow;
        var contest = TestEntities.CreateContest($"Login contest {Guid.NewGuid():N}", now.AddHours(started ? -1 : 1), now.AddHours(2));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            db.Contests.Add(contest);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var participant = await AddParticipantAsync(contest);
        using var client = factory.CreateSiteClient();
        string loginUrl = returnUrl is null ? "/Account/Login" : QueryHelpers.AddQueryString("/Account/Login", "ReturnUrl", returnUrl);
        using var response = await WebAppFactory.PostFormAsync(client, loginUrl, "login", new()
        {
            ["Input.Login"] = participant.UserName!,
            ["Input.Password"] = WebAppFactory.Password
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        string expected = returnUrl == "/submissions" ? returnUrl : $"/contests/{contest.Id}/rules";
        Assert.Equal(expected, new Uri(client.BaseAddress!, response.Headers.Location!).PathAndQuery);
    }

    [Theory]
    [InlineData(false, false, true, false, true)]
    [InlineData(true, false, true, false, false)]
    [InlineData(false, true, true, false, false)]
    [InlineData(false, false, false, false, false)]
    [InlineData(false, false, true, true, false)]
    public async Task StandingLinkUsesCurrentParticipationStatusAndAvailableTable(bool outOfCompetition, bool disqualified, bool approved, bool onlySavedFullTable, bool official)
    {
        var token = TestContext.Current.CancellationToken;
        var contest = TestEntities.CreateContest("Participant standings", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(2));
        if (onlySavedFullTable) contest.FinalTable = new Blob { Data = "<table><tr><td>Saved</td></tr></table>"u8.ToArray() };
        using (var setup = factory.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<OlympContext>();
            db.Contests.Add(contest);
            await db.SaveChangesAsync(token);
        }
        var participant = await AddParticipantAsync(contest);
        using var client = await factory.LoginAsync(participant.UserName!);
        using (var setup = factory.Services.CreateScope())
            await setup.ServiceProvider.GetRequiredService<OlympContext>().Competitors.Where(item => item.Id == participant.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.IsOutOfCompetition, outOfCompetition)
                    .SetProperty(item => item.IsDisqualified, disqualified).SetProperty(item => item.IsApproved, approved), token);

        using var response = await client.GetAsync($"/contests/{contest.Id}/rules", token);
        response.EnsureSuccessStatusCode();
        using var html = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
        string expected = $"contests/{contest.Id}/standing?highlight=true" + (official ? "&official=true" : "");
        Assert.NotNull(html.QuerySelector($".contest-header a[href='{expected}']"));
        using var standing = await client.GetAsync("/" + expected, token);
        standing.EnsureSuccessStatusCode();
        if (approved && !onlySavedFullTable)
        {
            using var results = await new HtmlParser().ParseDocumentAsync(await standing.Content.ReadAsStringAsync(token), token);
            Assert.True(results.QuerySelector("input[name='highlight']")!.HasAttribute("checked"));
            Assert.Equal(participant.Name, Assert.Single(results.QuerySelectorAll("tbody span.text-primary-emphasis")).TextContent);
        }
    }

    private async Task<Competitor> AddParticipantAsync(Contest contest)
    {
        var participant = TestEntities.CreateCompetitor(contest, $"temp_{Guid.NewGuid():N}");
        participant.UserName = participant.Name;
        participant.Contest = null!;
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.True((await users.CreateAsync(participant, WebAppFactory.Password)).Succeeded);
        Assert.True((await users.AddClaimAsync(participant,
            new Claim(ClaimNames.CompetitorContest, contest.Id.ToString()))).Succeeded);
        return participant;
    }
}

using System.Net;
using System.Security.Claims;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

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
    public async Task TemporaryAccountHasContestHeaderAndOwnNavigationWithoutChangingPublicAccess(string culture, string state)
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

        using var client = await factory.LoginAsync(participant.UserName);
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
            Assert.Equal(contest.Name, navbar.QuerySelector($"h1 a[href='contests/{contest.Id}/{homeSection}']")!.TextContent);
            Assert.Equal(contest.Name, navbar.QuerySelector("h1")!.TextContent.Trim());
            string countdown = state switch
            {
                "upcoming" => culture == "ru" ? "До начала " : "Starts in ",
                "running" => culture == "ru" ? "До конца " : "Ends in ",
                _ => culture == "ru" ? "соревнование закончено" : "contest is over"
            };
            Assert.StartsWith(countdown, navbar.QuerySelector(".small.text-body-secondary")!.TextContent.Trim());
            Assert.Null(document.QuerySelector(".contest-header h1"));
            var menu = document.QuerySelector(".contest-header nav")!;
            Assert.Equal(culture == "ru" ? "Мои решения" : "My submissions", menu.QuerySelector("a[href='submissions']")!.TextContent);
            Assert.Equal(state != "upcoming", menu.QuerySelector($"a[href='contests/{contest.Id}/problems']") is not null);
            Assert.Equal(state != "upcoming", menu.QuerySelector($"a[href='contests/{contest.Id}/standing']") is not null);
            Assert.Equal($"contests/{contest.Id}/{section}", menu.QuerySelector("a[aria-current='page']")!.GetAttribute("href"));
            Assert.Equal(state == "running", menu.QuerySelector($"a[href='contests/{contest.Id}/questions']") is not null);
            Assert.Equal(state == "upcoming" ? 2 : state == "running" ? 5 : 4, menu.QuerySelectorAll("a").Length);
            if (section == "rules")
                Assert.DoesNotContain(culture == "ru" ? "Вход для участия" : "Signing In to Participate", document.QuerySelector("main")!.TextContent);
        }

        using var history = await client.GetAsync("/submissions", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var historyDocument = await new HtmlParser().ParseDocumentAsync(
            await history.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal("submissions", historyDocument.QuerySelector(".contest-header a[aria-current='page']")!.GetAttribute("href"));
        Assert.Null(historyDocument.QuerySelector("main h1"));
        Assert.Null(historyDocument.QuerySelector("#submissions .nav"));

        using var foreign = await client.GetAsync($"/contests/{other.Id}/problems", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, foreign.StatusCode);
        using var foreignDocument = await new HtmlParser().ParseDocumentAsync(
            await foreign.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(other.Name, foreignDocument.QuerySelector(".contest-header h1")!.TextContent);
        Assert.NotNull(foreignDocument.QuerySelector($".contest-header a[href='contests/{other.Id}/competitors']"));
        Assert.Null(foreignDocument.QuerySelector(".contest-header a[href='submissions']"));
        Assert.Null(foreignDocument.QuerySelector("#submission-code"));

        using var foreignRules = await client.GetAsync($"/contests/{other.Id}/rules", TestContext.Current.CancellationToken);
        using var rulesDocument = await new HtmlParser().ParseDocumentAsync(
            await foreignRules.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(culture == "ru" ? "Вход для участия" : "Signing In to Participate", rulesDocument.QuerySelector("main")!.TextContent);
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

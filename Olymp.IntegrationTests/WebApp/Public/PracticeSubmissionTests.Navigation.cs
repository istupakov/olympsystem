using System.Net;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Public;

public partial class PracticeSubmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContestSubmissionsAreUnavailableBeforeStart(bool temporary)
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = await db.Contests.SingleAsync(TestContext.Current.CancellationToken);
        contest.StartTime = DateTimeOffset.UtcNow.AddHours(1);
        contest.EndTime = contest.StartTime.AddHours(3);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = temporary ? await TemporaryClientAsync(contest) : await factory.LoginAsync(data.User.UserName!);
        using var response = await client.GetAsync($"/contests/{contest.Id}/submissions", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var general = await GetAsync(client, "/submissions");
        Assert.Empty(general.QuerySelectorAll("#submissions tbody tr"));
        using var rules = await GetAsync(client, $"/contests/{contest.Id}/rules");
        Assert.Null(rules.QuerySelector($"a[href^='contests/{contest.Id}/submissions']"));
    }

    [Fact]
    public async Task DailyBannerShowsAllTodaysContestsInStartOrderAndExcludesHiddenAndOtherDays()
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Date, now.Offset);
        var running = await db.Contests.SingleAsync(TestContext.Current.CancellationToken);
        running.StartTime = today.AddTicks(1);
        running.EndTime = today.AddDays(1).AddTicks(-1);
        var finished = TestEntities.CreateContest("Finished today", today, now.AddTicks(-1));
        var upcoming = TestEntities.CreateContest("Upcoming today", today.AddDays(1).AddTicks(-2), today.AddDays(1).AddHours(1));
        var hidden = TestEntities.CreateContest("Hidden today", today, today.AddHours(1));
        hidden.IsHidden = true;
        db.AddRange(finished, upcoming, hidden,
            TestEntities.CreateContest("Yesterday", today.AddDays(-1), today.AddTicks(-1)),
            TestEntities.CreateContest("Tomorrow", today.AddDays(1), today.AddDays(1).AddHours(1)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = factory.CreateSiteClient();
        using var page = await GetAsync(client, "/");
        Assert.Equal([
            $"contests/{finished.Id}/standing",
            $"contests/{data.Contest.Id}/standing",
            $"contests/{upcoming.Id}/rules"
        ], page.QuerySelectorAll("header section a").Select(link => link.GetAttribute("href")));
        Assert.Equal(["badge text-bg-secondary", "badge text-bg-success", "badge text-bg-primary"],
            page.QuerySelectorAll("header section .badge").Select(badge => badge.ClassName));
    }

    [Theory]
    [InlineData(-1, 1, "Running", true)]
    [InlineData(1, 2, "Upcoming", false)]
    [InlineData(-2, -1, "contest is over", true)]
    public async Task DailyBannerShowsStateAndLinksToRulesOrStanding(int startHours, int endHours, string status, bool published)
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = await db.Contests.SingleAsync(TestContext.Current.CancellationToken);
        // Anchor the state to now; keep both endpoints on today's date even near midnight.
        var now = DateTimeOffset.Now;
        contest.StartTime = startHours > 0 ? now.AddTicks(1) : new DateTimeOffset(now.Date, now.Offset);
        contest.EndTime = endHours > 0 ? new DateTimeOffset(now.Date.AddDays(1).AddTicks(-1), now.Offset) : now.AddTicks(-1);
        if (startHours > 0)
            contest.StartTime = new DateTimeOffset(now.Date.AddDays(1).AddTicks(-2), now.Offset);
        contest.ProblemPdf = new Blob { Data = [37, 80, 68, 70] };
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        using var page = await GetAsync(client, "/");
        var banner = page.QuerySelector("header section");
        Assert.NotNull(banner);
        Assert.Contains(status, banner.TextContent);
        var link = Assert.Single(banner.QuerySelectorAll("a"));
        Assert.Equal($"contests/{data.Contest.Id}/{(published ? "standing" : "rules")}", link.GetAttribute("href"));
        Assert.Null(banner.QuerySelector("a[href*='/problems']"));
        Assert.Null(banner.QuerySelector("a[href^='submissions?']"));
        contest.IsHidden = true;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var hidden = await GetAsync(client, "/");
        Assert.Null(hidden.QuerySelector("header section"));
    }

    [Fact]
    public async Task SingleAvailableSubmissionScopeDoesNotShowSelector()
    {
        var data = await SeedAsync();
        using var client = await factory.LoginAsync(data.User.UserName!);
        using var filtered = await GetAsync(client, $"/contests/{data.Contest.Id}/submissions");
        Assert.Null(filtered.QuerySelector("#submissions nav"));
        using var general = await GetAsync(client, "/submissions");
        Assert.Equal(2, general.QuerySelectorAll("#submissions nav a").Length);
    }

    [Fact]
    public async Task ContestLinksUseDefaultScopeAndHideEmptyOwnScope()
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var author = await db.Competitors.SingleAsync(TestContext.Current.CancellationToken);
        await db.Entry(author).Collection(item => item.Members).LoadAsync(TestContext.Current.CancellationToken);
        author.Members.Clear();
        author.CoachId = data.User.Id;
        var submission = TestEntities.CreateSubmission(author, data.Problem, data.Compiler, DateTimeOffset.UtcNow);
        submission.Problem = null!;
        submission.Compilator = null!;
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = await factory.LoginAsync(data.User.UserName!);
        string path = $"/contests/{data.Contest.Id}/rules";
        using var rules = await GetAsync(client, path);
        Assert.NotNull(rules.QuerySelector($"main a[href='contests/{data.Contest.Id}/submissions']"));
        using var list = await GetAsync(client, $"/contests/{data.Contest.Id}/submissions?scope=coached");
        Assert.NotNull(list.QuerySelector("#submissions nav a[aria-current='page'][href*='scope=coached']"));
        Assert.Equal(2, list.QuerySelectorAll("#submissions nav a").Length);
        Assert.Single(list.QuerySelectorAll("#submissions tbody tr"));
        Assert.NotNull(list.QuerySelector($"main a[href='contests/{data.Contest.Id}/problems']"));
        using var defaultScope = await GetAsync(client, $"/contests/{data.Contest.Id}/submissions");
        Assert.NotNull(defaultScope.QuerySelector("#submissions nav a[aria-current='page'][href*='scope=coached']"));
        using var explicitEmpty = await GetAsync(client, $"/contests/{data.Contest.Id}/submissions?scope=own");
        Assert.Empty(explicitEmpty.QuerySelectorAll("#submissions tbody tr"));

        var ownSubmission = TestEntities.CreateSubmission(data.User, data.Problem, data.Compiler, DateTimeOffset.UtcNow);
        ownSubmission.User = null!;
        ownSubmission.Problem = null!;
        ownSubmission.Compilator = null!;
        db.Submissions.Add(ownSubmission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var ownDefault = await GetAsync(client, $"/contests/{data.Contest.Id}/submissions");
        Assert.NotNull(ownDefault.QuerySelector($"#submission-{ownSubmission.Id}"));
        Assert.Null(ownDefault.QuerySelector($"#submission-{submission.Id}"));
        Assert.DoesNotContain("scope=", ownDefault.QuerySelector("#submissions nav a[aria-current='page']")!.GetAttribute("href")!);
    }

    [Fact]
    public async Task ParticipantNavigationKeepsPdfAndFiltersOwnSubmissions()
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = await db.Contests.SingleAsync(TestContext.Current.CancellationToken);
        contest.ProblemPdf = new Blob { Data = [37, 80, 68, 70] };
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = await TemporaryClientAsync(contest);
        using var page = await GetAsync(client, ProblemPath(contest.Id, data.Problem.Id));
        Assert.NotNull(page.QuerySelector($"main a[href='contests/{contest.Id}/downloadproblems']"));
        Assert.NotNull(page.QuerySelector($"main a[href='contests/{contest.Id}/submissions']"));
        using var pdf = await client.GetAsync($"/contests/{contest.Id}/downloadproblems", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
    }
}

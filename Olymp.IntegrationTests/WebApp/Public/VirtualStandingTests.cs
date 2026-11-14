using System.Globalization;
using System.Net;
using System.Text;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp.Components.Contests;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Public;

[Trait("Category", "Integration")]
public class VirtualStandingTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task FormCreatesShareableOffsetUrlWithoutCreatingAContestOrRegistration(string culture)
    {
        var contest = Contest();
        await SaveAsync(contest);
        using var client = _factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        string path = $"/contests/{contest.Id}/virtual";
        using var rules = await GetAsync(client, $"/contests/{contest.Id}/rules");
        Assert.NotNull(rules.QuerySelector($"a[href='contests/{contest.Id}/virtual']"));
        using var form = await GetAsync(client, path);
        Assert.NotNull(form.QuerySelector("input[type='datetime-local']"));
        Assert.Contains(culture == "ru" ? "Часовой пояс" : "time zone", form.QuerySelector(".form-text")!.TextContent);
        Assert.Contains(culture == "ru" ? "личного аккаунта" : "personal account", form.QuerySelector("main")!.TextContent);
        Assert.DoesNotContain("<!--Blazor:", form.DocumentElement.OuterHtml);

        using var posted = await WebAppFactory.PostFormAsync(client, path, "virtual-contest", new()
        {
            ["Input.StartDate"] = "2026-12-01T10:30"
        });
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        var url = new Uri(client.BaseAddress!, posted.Headers.Location!);
        var query = QueryHelpers.ParseQuery(url.Query);
        var start = DateTimeOffset.ParseExact(query["start"]!, "O", CultureInfo.InvariantCulture);
        Assert.Equal(new DateTime(2026, 12, 1, 10, 30, 0), start.DateTime);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(start.DateTime), start.Offset);
        using var table = await GetAsync(client, url.PathAndQuery);
        Assert.Null(table.QuerySelector(".standing-view"));
        Assert.Equal(url.AbsoluteUri, table.QuerySelector("a[data-enhance-nav='false']")!.GetAttribute("href"));
        using var edit = await GetAsync(client, QueryHelpers.AddQueryString(url.PathAndQuery, "edit", "true"));
        Assert.StartsWith("2026-12-01T10:30", edit.QuerySelector("#virtual-start")!.GetAttribute("value"));
        Assert.Null(edit.QuerySelector("main table"));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.Equal(1, await db.Contests.CountAsync(item => item.Name == contest.Name, TestContext.Current.CancellationToken));
        Assert.Empty(await db.Competitors.Where(item => item.ContestId == contest.Id).ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplayUsesRelativeTimeIgnoresSavedHtmlAndFreezeAndKeepsRefreshUrl()
    {
        var contest = Contest();
        contest.FreezeTime = contest.StartTime.AddMinutes(5);
        contest.FinalTable = new Blob { Data = Encoding.UTF8.GetBytes("<p>ARCHIVE_ONLY</p>") };
        var original = TestEntities.CreateCompetitor(contest, "Original team");
        contest.Competitors.Add(original);
        var personal = Personal("Virtual author");
        var problem = TestEntities.CreateProblem(contest);
        contest.Problems.Add(problem);
        var start = DateTimeOffset.UtcNow.AddMinutes(-40).ToOffset(TimeSpan.FromHours(7));
        Submit(problem, original, contest.StartTime.AddMinutes(30), 2);
        Submit(problem, personal, start.AddMinutes(20), 2);
        var later = TestEntities.CreateProblem(contest, 2);
        contest.Problems.Add(later);
        Submit(later, original, contest.StartTime.AddHours(1), 2);
        await SaveAsync(contest);
        using var client = _factory.CreateSiteClient();
        using var page = await GetAsync(client, Url(contest.Id, start));
        var rows = page.QuerySelectorAll("tbody tr");
        Assert.Equal(2, rows.Length);
        Assert.Contains(personal.Name, rows[0].TextContent);
        Assert.NotNull(rows[0].QuerySelector(".badge"));
        Assert.Contains(original.Name, rows[1].TextContent);
        Assert.Contains("+", rows[1].TextContent);
        Assert.Equal("table-success text-center", rows[0].QuerySelectorAll("td")[2].ClassName);
        Assert.DoesNotContain("ARCHIVE_ONLY", page.DocumentElement.TextContent);
        Assert.DoesNotContain("PRIVATE_SOURCE", page.DocumentElement.TextContent);
        Assert.Null(page.QuerySelector("caption .text-bg-warning"));
        Assert.Null(page.QuerySelector(".standing-view"));
        Assert.Equal(["2/2", "0/0"], page.QuerySelectorAll("tfoot td").Take(2).Select(cell => cell.TextContent));
        var refresh = page.QuerySelector("a[data-enhance-nav='false']")!.GetAttribute("href")!;
        Assert.Equal(start, DateTimeOffset.ParseExact(QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, refresh).Query)["start"]!, "O", CultureInfo.InvariantCulture));
        Assert.Equal(2, page.QuerySelectorAll("main time").Count(item => item.GetAttribute("datetime") is string value && value.StartsWith(start.Year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FutureStartShowsCountdownAndZeroResultsWithoutReplayingOriginalAttempts()
    {
        var contest = Contest();
        var original = TestEntities.CreateCompetitor(contest, "Original participant");
        contest.Competitors.Add(original);
        var problem = TestEntities.CreateProblem(contest);
        contest.Problems.Add(problem);
        Submit(problem, original, contest.StartTime, 2);
        await SaveAsync(contest);
        using var client = _factory.CreateSiteClient();
        using var page = await GetAsync(client, Url(contest.Id, DateTimeOffset.UtcNow.AddHours(2)));
        Assert.Contains("Starts in", page.QuerySelector("caption")!.TextContent);
        var row = Assert.Single(page.QuerySelectorAll("tbody tr"));
        Assert.Equal("0", row.QuerySelector("th")!.TextContent);
        Assert.DoesNotContain("+", row.TextContent);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("hidden")]
    [InlineData("upcoming")]
    public async Task VirtualPagesRequireVisibleOpenContestWithPublishedStandings(string reason)
    {
        var contest = Contest();
        if (reason == "closed") contest.IsOpen = false;
        if (reason == "hidden") contest.IsHidden = true;
        if (reason == "upcoming") { contest.StartTime = DateTimeOffset.UtcNow.AddDays(1); contest.EndTime = contest.StartTime.AddHours(5); }
        await SaveAsync(contest);
        using var client = _factory.CreateSiteClient();
        foreach (string path in new[] { $"/contests/{contest.Id}/virtual", Url(contest.Id, DateTimeOffset.UtcNow) })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        if (reason != "hidden")
        {
            using var rules = await GetAsync(client, $"/contests/{contest.Id}/rules");
            Assert.Null(rules.QuerySelector($"a[href='contests/{contest.Id}/virtual']"));
        }
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("2026-12-01T10:30:00")]
    [InlineData("9999-12-31T23:00:00.0000000+00:00")]
    public async Task InvalidOrUnzonedStartShowsLocalizedErrorAndTheForm(string start)
    {
        var contest = Contest();
        await SaveAsync(contest);
        using var client = _factory.CreateSiteClient();
        using var page = await GetAsync(client, QueryHelpers.AddQueryString($"/contests/{contest.Id}/virtual", "start", start));
        Assert.Contains("valid date", page.QuerySelector("[role='alert']")!.TextContent);
        Assert.NotNull(page.QuerySelector("form"));
        Assert.Null(page.QuerySelector("main table"));
    }

    [Fact]
    public async Task SavedHtmlDoesNotPreventAnEmptyVirtualTableOrPlayingWithoutOriginalSubmissions()
    {
        var contest = Contest();
        contest.FinalTable = new Blob { Data = Encoding.UTF8.GetBytes("<p>ARCHIVE_ONLY</p>") };
        contest.Problems.Add(TestEntities.CreateProblem(contest));
        await SaveAsync(contest);
        using var client = _factory.CreateSiteClient();
        using var page = await GetAsync(client, Url(contest.Id, DateTimeOffset.UtcNow.AddYears(100)));
        Assert.Empty(page.QuerySelectorAll("tbody tr"));
        Assert.Equal("0/0", page.QuerySelector("tfoot td")!.TextContent);
        Assert.DoesNotContain("ARCHIVE_ONLY", page.DocumentElement.TextContent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedTableShowsUnknownAttemptsOnlyOutsideSchoolModeAndAggregatesAttempts(bool school)
    {
        var contest = Contest();
        if (school) contest.Abbr = "school-test";
        var first = TestEntities.CreateCompetitor(contest, "First");
        var second = TestEntities.CreateCompetitor(contest, "Second");
        contest.Competitors.Add(first);
        contest.Competitors.Add(second);
        var problem = TestEntities.CreateProblem(contest);
        contest.Problems.Add(problem);
        Submit(problem, first, contest.StartTime.AddMinutes(1), -1001);
        Submit(problem, first, contest.StartTime.AddMinutes(2), 0);
        Submit(problem, first, contest.StartTime.AddMinutes(3), 1);
        Submit(problem, first, contest.StartTime.AddMinutes(4), -1);
        Submit(problem, second, contest.StartTime.AddMinutes(5), 2);
        await SaveAsync(contest);
        using var client = _factory.CreateSiteClient();
        using var page = await GetAsync(client, $"/contests/{contest.Id}/standing");
        var row = page.QuerySelectorAll("tbody tr").Single(item => item.TextContent.Contains(first.Name, StringComparison.Ordinal));
        var cell = row.QuerySelectorAll("td")[2];
        if (school)
        {
            Assert.DoesNotContain("?", cell.TextContent);
            Assert.Contains("0/100", cell.TextContent);
        }
        else
        {
            Assert.Equal("?300:03", cell.TextContent.Trim());
            Assert.Contains("text-warning-emphasis", cell.ClassName);
            Assert.DoesNotContain("table-warning", cell.ClassName);
            Assert.Null(cell.QuerySelector("[title], abbr"));
        }
        Assert.Equal("1/4", page.QuerySelector("tfoot td")!.TextContent);

        // The jury uses the same component with freeze disabled.
        var standing = Standing.Create(contest, true, false, true, null);
        using var juryTable = await new HtmlParser().ParseDocumentAsync(await RenderAsync<StandingTable>("/jury/contests/1/standing", new() { [nameof(StandingTable.Standing)] = standing }));
        Assert.Equal("1/4", juryTable.QuerySelector("tfoot td")!.TextContent);
        Assert.Equal(!school, juryTable.QuerySelectorAll("tbody td").Any(item => item.TextContent.Trim() == "?300:03"));
    }

    [Theory]
    [InlineData(0, false, "00:10")]
    [InlineData(2, true, "01:00")]
    public async Task UnknownOnlyCellShowsTheLastAttemptTimeWithoutAWarningBackground(int status, bool frozen, string time)
    {
        var contest = Contest();
        var participant = TestEntities.CreateCompetitor(contest, "Unknown attempt");
        contest.Competitors.Add(participant);
        var problem = TestEntities.CreateProblem(contest);
        contest.Problems.Add(problem);
        if (frozen) contest.FreezeTime = contest.StartTime.AddHours(1);
        Submit(problem, participant, contest.StartTime.AddMinutes(frozen ? 60 : 10), status);
        var standing = Standing.Create(contest, false, true, false, null);

        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<StandingTable>("/contests/1/standing", new() { [nameof(StandingTable.Standing)] = standing }));
        var cell = page.QuerySelectorAll("tbody td")[2];
        Assert.Equal($"?1{time}", cell.TextContent.Trim());
        Assert.Contains("text-warning-emphasis", cell.ClassName);
        Assert.DoesNotContain("table-warning", cell.ClassName);
        Assert.NotNull(cell.QuerySelector("br"));
        Assert.Equal("00:00:00", page.QuerySelectorAll("tbody td")[3].TextContent);
    }

    private static Contest Contest()
    {
        var contest = TestEntities.CreateContest($"Virtual {Guid.NewGuid():N}",
            new DateTimeOffset(2020, 1, 1, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2020, 1, 1, 15, 0, 0, TimeSpan.Zero));
        contest.IsOpen = true;
        return contest;
    }

    private async Task SaveAsync(Contest contest)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Add(contest);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static OlympUser Personal(string name) => new() { Name = name, Memberships = [], Teams = [], Messages = [], Submissions = [] };

    private static void Submit(Problem problem, User user, DateTimeOffset time, int status)
    {
        var submission = TestEntities.CreateSubmission(user, problem, TestEntities.CreateCompilator(), time, Encoding.UTF8.GetBytes("PRIVATE_SOURCE"));
        submission.StatusCode = status;
        problem.Submissions.Add(submission);
    }

    private static string Url(int id, DateTimeOffset start) => QueryHelpers.AddQueryString($"/contests/{id}/virtual", "start", start.ToString("O", CultureInfo.InvariantCulture));

    private static async Task<AngleSharp.Dom.IDocument> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
    }
}

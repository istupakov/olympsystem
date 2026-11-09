using System.Globalization;
using System.Net;
using System.Text;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class ContestPublicPagesTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("ru", "Команда", "Тренер")]
    [InlineData("en", "Team", "Coach")]
    public async Task TeamParticipantsPreserveNamesAndDoNotExposeAccountData(string culture, string teamLabel, string coachLabel)
    {
        var contest = CreateContest();
        var team = AddCompetitor(contest, "Team <script>name</script>");
        team.RegistrationDate = new DateTimeOffset(2026, 1, 17, 12, 30, 0, TimeSpan.Zero);
        team.MemberInfos = "Historical First;;Historical Second;;Historical Third;";
        team.Organization = new Organization { Name = "University", FullName = "Full university", Users = [] };
        team.Coach = new OlympUser
        {
            Name = "Coach account",
            LastName = "Иванов",
            FirstName = "Иван",
            Patronymic = "Иванович",
            Email = "private-coach@example.test",
            UserName = "private-coach-login",
            Memberships = [],
            Teams = [],
            Messages = [],
            Submissions = []
        };
        AddCompetitor(contest, "Hidden participant").IsHidden = true;
        await SaveAsync(contest);
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync($"/contests/{contest.Id}/competitors", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = await new HtmlParser().ParseDocumentAsync(html, TestContext.Current.CancellationToken);
        Assert.Contains(teamLabel, document.QuerySelectorAll("main th").Select(header => header.TextContent));
        Assert.Contains(coachLabel, document.QuerySelectorAll("main th").Select(header => header.TextContent));
        var row = Assert.Single(document.QuerySelectorAll("main tbody tr"));
        Assert.Equal(team.Name, row.QuerySelector("td .fw-semibold")!.TextContent);
        Assert.Equal("University", row.QuerySelector("td abbr")!.TextContent);
        Assert.Equal("Full university", row.QuerySelector("td abbr")!.GetAttribute("title"));
        Assert.Equal(5, row.QuerySelectorAll("td").Length);
        Assert.Equal(culture == "ru" ? "Дата регистрации" : "Registration date", document.QuerySelector("main th:last-child")!.TextContent);
        Assert.Equal(team.RegistrationDate.Value.ToLocalTime().ToString("d", CultureInfo.GetCultureInfo(culture)),
            row.QuerySelector("td:last-child")!.TextContent);
        Assert.Equal(["Historical First", "Historical Second", "Historical Third"], row.QuerySelectorAll("li").Select(item => item.TextContent));
        Assert.Contains("Иванов Иван Иванович", row.TextContent);
        Assert.Empty(row.QuerySelectorAll(".badge"));
        Assert.Null(document.QuerySelector("main script"));
        Assert.DoesNotContain("private-coach", html);
        Assert.DoesNotContain("Hidden participant", html);
        Assert.DoesNotContain("<!--Blazor:", html);
    }

    [Fact]
    public async Task IndividualParticipantsIncludePendingAndOutOfCompetitionStatuses()
    {
        var contest = CreateContest();
        contest.Abbr = "school-test";
        AddCompetitor(contest, "Approved");
        var pending = AddCompetitor(contest, "Pending");
        pending.IsApproved = false;
        pending.IsOutOfCompetition = true;
        pending.IsDisqualified = true;
        await SaveAsync(contest);
        using var client = factory.CreateSiteClient();
        using var response = await client.GetAsync($"/contests/{contest.Id}/competitors", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(2, document.QuerySelectorAll("main ol > li").Length);
        Assert.Empty(document.QuerySelector("main ol > li")!.QuerySelectorAll(".badge"));
        Assert.Contains("Pending approval", document.QuerySelector("main ol")!.TextContent);
        Assert.Contains("Out of competition", document.QuerySelector("main ol")!.TextContent);
        Assert.Contains("Disqualified", document.QuerySelector("main ol")!.TextContent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParticipantsAreOrderedByCompetitionStatusThenUniversityThenName(bool teamContest)
    {
        var contest = CreateContest();
        if (!teamContest)
            contest.Abbr = "school-test";
        var firstUniversity = new Organization { Name = "First university", Users = [] };
        var secondUniversity = new Organization { Name = "Second university", Users = [] };
        var lastGuest = AddCompetitor(contest, "A guest");
        lastGuest.Organization = secondUniversity;
        lastGuest.IsOutOfCompetition = true;
        AddCompetitor(contest, "C main").Organization = secondUniversity;
        var firstGuest = AddCompetitor(contest, "B guest");
        firstGuest.Organization = firstUniversity;
        firstGuest.IsOutOfCompetition = true;
        AddCompetitor(contest, "Z main").Organization = firstUniversity;
        AddCompetitor(contest, "B main").Organization = secondUniversity;
        await SaveAsync(contest);
        using var client = factory.CreateSiteClient();
        using var response = await client.GetAsync($"/contests/{contest.Id}/competitors", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var names = document.QuerySelectorAll(teamContest ? "main tbody tr td:first-child .fw-semibold" : "main ol > li")
            .Select(item => item.ChildNodes.First().TextContent.Trim());
        Assert.Equal(["Z main", "B main", "C main", "B guest", "A guest"], names);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task StandingsReuseScoringAndSwitchBetweenAllAndOfficialParticipants(string culture)
    {
        var contest = CreateContest();
        var alice = AddCompetitor(contest, "Alice");
        var bob = AddCompetitor(contest, "Bob");
        bob.IsOutOfCompetition = true;
        var chris = AddCompetitor(contest, "Chris");
        chris.IsDisqualified = true;
        var hidden = AddCompetitor(contest, "Hidden");
        hidden.IsHidden = true;
        var practice = new OlympUser { Name = "Practice", Memberships = [], Teams = [], Messages = [], Submissions = [] };
        var problem = AddProblem(contest);
        AddSubmission(problem, practice, contest.StartTime.AddMinutes(25), 2);
        AddSubmission(problem, alice, contest.StartTime.AddMinutes(10), 5);
        AddSubmission(problem, alice, contest.StartTime.AddMinutes(20), 2);
        AddSubmission(problem, bob, contest.StartTime.AddMinutes(5), 2);
        AddSubmission(problem, chris, contest.StartTime.AddMinutes(8), 2);
        AddSubmission(problem, hidden, contest.StartTime.AddMinutes(1), 2);
        await SaveAsync(contest);
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var all = await client.GetAsync($"/contests/{contest.Id}/standing", TestContext.Current.CancellationToken);
        all.EnsureSuccessStatusCode();
        var allHtml = await all.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = await new HtmlParser().ParseDocumentAsync(allHtml, TestContext.Current.CancellationToken);
        var rows = document.QuerySelectorAll("main tbody tr");
        Assert.Equal(4, rows.Length);
        Assert.StartsWith("Bob", rows[0].QuerySelectorAll("td")[1].TextContent.Trim());
        Assert.StartsWith("Chris", rows[1].QuerySelectorAll("td")[1].TextContent.Trim());
        Assert.StartsWith("Practice", rows[2].QuerySelectorAll("td")[1].TextContent.Trim());
        Assert.StartsWith("Alice", rows[3].QuerySelectorAll("td")[1].TextContent.Trim());
        Assert.Contains("+1", rows[3].QuerySelectorAll("td")[2].TextContent);
        Assert.Equal("00:40:00", rows[3].QuerySelectorAll("td")[3].TextContent);
        Assert.DoesNotContain("Hidden", allHtml);
        Assert.DoesNotContain("PRIVATE_SOURCE", allHtml);
        Assert.Null(document.QuerySelector("main form"));
        Assert.Equal(culture == "ru" ? "Полная таблица" : "Full standings", document.QuerySelector(".standing-view a[aria-current='page']")!.TextContent);
        var officialLink = document.QuerySelector($"main nav a[href='contests/{contest.Id}/standing?official=true']")!;
        Assert.Equal(culture == "ru" ? "Официальная таблица" : "Official standings", officialLink.TextContent);
        using var official = await client.GetAsync("/" + officialLink.GetAttribute("href"), TestContext.Current.CancellationToken);
        official.EnsureSuccessStatusCode();
        using var officialDocument = await new HtmlParser().ParseDocumentAsync(
            await official.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.StartsWith("Alice", Assert.Single(officialDocument.QuerySelectorAll("main tbody tr")).QuerySelectorAll("td")[1].TextContent.Trim());
        Assert.Equal(culture == "ru" ? "Официальная таблица" : "Official standings", officialDocument.QuerySelector(".standing-view a[aria-current='page']")!.TextContent);
        Assert.Contains(culture == "ru" ? "Без внеконкурсных" : "Excludes out-of-competition", officialDocument.QuerySelector("main")!.TextContent);
        var fullLink = officialDocument.QuerySelector($"main nav a[href='contests/{contest.Id}/standing']")!;
        using var fullAgain = await client.GetAsync("/" + fullLink.GetAttribute("href"), TestContext.Current.CancellationToken);
        fullAgain.EnsureSuccessStatusCode();
        using var fullDocument = await new HtmlParser().ParseDocumentAsync(
            await fullAgain.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(4, fullDocument.QuerySelectorAll("main tbody tr").Length);
    }

    [Theory]
    [InlineData("ru", true)]
    [InlineData("en", true)]
    [InlineData("ru", false)]
    [InlineData("en", false)]
    public async Task StandingCaptionShowsTimeLeftOrCompletionAndLocalizedFreezeTime(string culture, bool ongoing)
    {
        var contest = CreateContest();
        if (ongoing)
            contest.EndTime = DateTimeOffset.UtcNow.AddHours(2);
        contest.IsOpen = !ongoing;
        contest.FreezeTime = contest.StartTime.AddHours(1).ToOffset(TimeSpan.FromHours(7));
        await SaveAsync(contest);
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        var before = DateTimeOffset.UtcNow;
        using var response = await client.GetAsync($"/contests/{contest.Id}/standing", TestContext.Current.CancellationToken);
        var after = DateTimeOffset.UtcNow;
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var caption = document.QuerySelector("main caption")!;
        var status = caption.QuerySelector(".text-bg-primary")!.TextContent;
        if (ongoing)
        {
            if (culture == "ru")
                Assert.StartsWith("осталось ", status);
            else
                Assert.EndsWith(" left", status);
            var text = status.Replace("осталось ", "").Replace(" left", "");
            var remaining = TimeSpan.Parse(text, CultureInfo.InvariantCulture);
            Assert.InRange(remaining.TotalSeconds, (contest.EndTime - after).TotalSeconds - 1, (contest.EndTime - before).TotalSeconds + 1);
        }
        else
        {
            Assert.Equal(culture == "ru" ? "соревнование закончено" : "contest is over", status);
        }
        var freezeTime = contest.FreezeTime.Value.ToString(culture == "ru" ? "HH:mm z" : "hh:mm tt z", CultureInfo.GetCultureInfo(culture));
        Assert.Contains(freezeTime, caption.QuerySelector(".text-bg-warning")!.TextContent);
    }

    [Fact]
    public async Task PublicStandingsRespectFreezeAndHiddenSubmissions()
    {
        var contest = CreateContest();
        contest.EndTime = DateTimeOffset.UtcNow.AddHours(2);
        contest.FreezeTime = DateTimeOffset.UtcNow.AddHours(-1);
        var participant = AddCompetitor(contest, "Frozen participant");
        var problem = AddProblem(contest);
        AddSubmission(problem, participant, contest.StartTime.AddMinutes(10), 5);
        AddSubmission(problem, participant, contest.StartTime.AddMinutes(15), 2).IsHidden = true;
        AddSubmission(problem, participant, contest.FreezeTime.Value.AddMinutes(5), 2);
        await SaveAsync(contest);
        using var client = factory.CreateSiteClient();
        using var response = await client.GetAsync($"/contests/{contest.Id}/standing", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Contains("Frozen at", document.QuerySelector("caption")!.TextContent);
        Assert.Equal("0", document.QuerySelector("main tbody th")!.TextContent);
        Assert.Contains("-1", document.QuerySelectorAll("main tbody td")[2].TextContent);
    }

    [Fact]
    public async Task SchoolStandingsShowPartialPoints()
    {
        var contest = CreateContest();
        contest.Abbr = "school-test";
        var participant = AddCompetitor(contest, "School participant");
        var problem = AddProblem(contest);
        AddSubmission(problem, participant, contest.StartTime.AddMinutes(10), 4).Score = 60;
        await SaveAsync(contest);
        using var client = factory.CreateSiteClient();
        using var response = await client.GetAsync($"/contests/{contest.Id}/standing", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Contains("Points", document.QuerySelector("thead")!.TextContent);
        Assert.Equal("60", document.QuerySelector("main tbody th")!.TextContent);
        Assert.Contains("60/100", document.QuerySelectorAll("main tbody td")[2].TextContent);
    }

    [Theory]
    [InlineData(false, "Архивная общая таблица")]
    [InlineData(true, "Архивная официальная таблица")]
    public async Task SavedTablesAreDecodedWithoutRecalculating(bool official, string text)
    {
        var contest = CreateContest();
        var encoding = CodePagesEncodingProvider.Instance.GetEncoding(1251)!;
        contest.FinalTable = new Blob { Data = encoding.GetBytes("<table class='result'><tbody><tr><td class='successmark'>Архивная общая таблица</td><td class='failmark'>-1</td><td class='firstmark'>+</td></tr></tbody></table>") };
        contest.OfficialTable = new Blob { Data = encoding.GetBytes("<table class='result'><tbody><tr><td class='successmark'>Архивная официальная таблица</td><td class='failmark'>-1</td><td class='firstmark'>+</td></tr></tbody></table>") };
        AddCompetitor(contest, "Should not replace archive");
        await SaveAsync(contest);
        using var client = factory.CreateSiteClient();
        using var response = await client.GetAsync($"/contests/{contest.Id}/standing?official={official.ToString().ToLowerInvariant()}", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(text, document.QuerySelector("main tbody td")!.TextContent);
        Assert.NotNull(document.QuerySelector("main [data-saved-standing] table.result"));
        Assert.Equal([text, "-1", "+"], document.QuerySelectorAll("main tbody td").Select(cell => cell.TextContent));
        using var script = await client.GetAsync("/Components/Contests/SavedStandingView.razor.js", TestContext.Current.CancellationToken);
        script.EnsureSuccessStatusCode();
        Assert.DoesNotContain("Should not replace archive", document.QuerySelector("main")!.TextContent);
    }

    [Fact]
    public async Task SavedTableWithoutOfficialVersionHasNoSwitch()
    {
        var contest = CreateContest();
        contest.FinalTable = new Blob { Data = Encoding.ASCII.GetBytes("<table><tr><td>Saved</td></tr></table>") };
        await SaveAsync(contest);
        using var client = factory.CreateSiteClient();
        using var response = await client.GetAsync($"/contests/{contest.Id}/standing", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Null(document.QuerySelector(".standing-view"));
        using var unavailable = await client.GetAsync($"/contests/{contest.Id}/standing?official=true", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task ContestSectionsShareHeaderNavigationAndExplicitDownloads(string culture)
    {
        var contest = CreateContest();
        contest.Name = "Contest <em>name</em>";
        contest.StartTime = new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.Zero);
        contest.EndTime = contest.StartTime.AddHours(5);
        contest.IsOpen = true;
        contest.ProblemPdf = new Blob { Data = [1] };
        contest.TestZip = new Blob { Data = [2] };
        await SaveAsync(contest);
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        foreach (string route in new[] { "standing", "competitors", "rules" })
        {
            using var response = await client.GetAsync($"/contests/{contest.Id}/{route}", TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = await new HtmlParser().ParseDocumentAsync(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            var header = document.QuerySelector("main .contest-header")!;
            Assert.Equal(contest.Name, header.QuerySelector("h1")!.TextContent);
            Assert.Null(header.QuerySelector("h1 em"));
            Assert.Equal($"contests/{contest.Id}/{route}", Assert.Single(header.QuerySelectorAll("a[aria-current='page']")).GetAttribute("href"));
            foreach (string sibling in new[] { "standing", "competitors", "rules" })
                Assert.NotNull(header.QuerySelector($"a[href='contests/{contest.Id}/{sibling}']"));
            Assert.Equal(culture == "ru" ? "Скачать условия (PDF)" : "Download statements (PDF)",
                header.QuerySelector($"a[href='contests/{contest.Id}/downloadproblems']")!.TextContent);
            Assert.Equal(culture == "ru" ? "Скачать тесты (ZIP)" : "Download tests (ZIP)",
                header.QuerySelector($"a[href='contests/{contest.Id}/downloadtests']")!.TextContent);
            Assert.Single(header.QuerySelectorAll("time[datetime]"));
        }
    }

    [Theory]
    [InlineData("ru", true, false)]
    [InlineData("en", true, false)]
    [InlineData("ru", true, true)]
    [InlineData("en", true, true)]
    [InlineData("ru", false, false)]
    [InlineData("en", false, false)]
    public async Task ContestHeaderShowsOnlyDatesAfterContestEnds(string culture, bool ended, bool spansDays)
    {
        var contest = CreateContest();
        contest.StartTime = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(ended ? -3 : 3).AddHours(10), TimeSpan.Zero);
        contest.EndTime = contest.StartTime.AddHours(spansDays ? 29 : 5);
        await SaveAsync(contest);
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync($"/contests/{contest.Id}/competitors", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var times = document.QuerySelectorAll(".contest-header time");
        var formatting = CultureInfo.GetCultureInfo(culture);
        if (ended)
        {
            Assert.Null(document.QuerySelector(".contest-header .fw-semibold"));
            Assert.Equal(spansDays ? 2 : 1, times.Length);
            Assert.Equal(contest.StartTime.ToString("d", formatting), times[0].TextContent);
            if (spansDays)
                Assert.Equal(contest.EndTime.ToString("d", formatting), times[1].TextContent);
        }
        else
        {
            Assert.Equal(2, times.Length);
            Assert.Equal(contest.StartTime.ToString("g", formatting), times[0].TextContent);
            Assert.Equal(contest.EndTime.ToString("t", formatting), times[1].TextContent);
        }
    }

    [Theory]
    [InlineData("ru", true, 275400, "До начала 3 д 4 ч")]
    [InlineData("en", true, 275400, "Starts in 3 d 4 h")]
    [InlineData("ru", true, 8130, "До начала 2 ч 15 мин")]
    [InlineData("en", true, 8130, "Starts in 2 h 15 min")]
    [InlineData("ru", false, 930, "До конца 15 мин")]
    [InlineData("en", false, 930, "Ends in 15 min")]
    [InlineData("ru", false, 45, "До конца [1-9][0-9]? с")]
    [InlineData("en", false, 45, "Ends in [1-9][0-9]? s")]
    public async Task ContestHeaderShowsLocalizedTimeUntilStartOrEnd(string culture, bool upcoming, int seconds, string expected)
    {
        var contest = CreateContest();
        var target = DateTimeOffset.UtcNow.AddSeconds(seconds);
        contest.StartTime = upcoming ? target : DateTimeOffset.UtcNow.AddHours(-1);
        contest.EndTime = upcoming ? target.AddHours(5) : target;
        contest.IsOpen = true;
        await SaveAsync(contest);
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync($"/contests/{contest.Id}/competitors", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Matches($"^{expected}$", document.QuerySelector(".contest-header .fw-semibold")!.TextContent);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task DescriptionIsPublishedAboveStandingsOnlyWhenOpen(bool open, bool savedTable)
    {
        var contest = CreateContest();
        contest.IsOpen = open;
        contest.Description = "<p><strong>Prize places</strong></p>";
        if (savedTable)
            contest.FinalTable = new Blob { Data = Encoding.ASCII.GetBytes("<table><tr><td>Saved result</td></tr></table>") };
        await SaveAsync(contest);
        using var client = factory.CreateSiteClient();
        using var response = await client.GetAsync($"/contests/{contest.Id}/standing", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = await new HtmlParser().ParseDocumentAsync(html, TestContext.Current.CancellationToken);
        Assert.Equal(open, document.QuerySelector(".contest-description") is not null);
        if (open)
        {
            Assert.Equal("Prize places", document.QuerySelector(".contest-description strong")!.TextContent);
            Assert.True(html.IndexOf("Prize places", StringComparison.Ordinal) < html.IndexOf("<table", StringComparison.Ordinal));
        }
        else
            Assert.DoesNotContain("Prize places", html);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    public async Task TaskLinksLeadToPublishedContestWorkspace(bool open, bool ended, bool signedIn)
    {
        var contest = CreateContest();
        contest.IsOpen = open;
        if (!ended)
            contest.EndTime = DateTimeOffset.UtcNow.AddHours(1);
        AddProblem(contest).IsActive = true;
        await SaveAsync(contest);
        using var client = signedIn ? await factory.LoginAsync((await factory.AddUserAsync()).UserName!) : factory.CreateSiteClient();
        foreach (string path in new[] { "/contests", $"/contests/{contest.Id}/competitors" })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = await new HtmlParser().ParseDocumentAsync(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            var container = path == "/contests"
                ? document.QuerySelector($"article:has(a[href='contests/{contest.Id}/rules'])")!
                : document.QuerySelector(".contest-header")!;
            var link = container.QuerySelector($"a[href='contests/{contest.Id}/problems']");
            Assert.NotNull(link);
            if (link is not null)
            {
                var query = QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, link.GetAttribute("href")).Query);
                Assert.Equal($"contests/{contest.Id}/problems", link.GetAttribute("href"));
                Assert.Empty(query);
                string workspacePath = "/" + link.GetAttribute("href");
                using var workspace = await client.GetAsync(workspacePath, TestContext.Current.CancellationToken);
                if (signedIn)
                    workspace.EnsureSuccessStatusCode();
                else
                {
                    Assert.Equal(HttpStatusCode.Redirect, workspace.StatusCode);
                    var login = new Uri(client.BaseAddress!, workspace.Headers.Location!);
                    Assert.Equal("/Account/Login", login.AbsolutePath);
                    Assert.Equal(workspacePath, QueryHelpers.ParseQuery(login.Query)["ReturnUrl"]);
                }
            }
        }
    }

    [Theory]
    [InlineData("downloadproblems", "application/pdf", "problems.pdf", false)]
    [InlineData("downloadtests", "application/zip", "tests.zip", true)]
    public async Task AvailableMaterialsAreDownloadedWithOriginalNames(string path, string contentType, string suffix, bool open)
    {
        var contest = CreateContest();
        contest.IsOpen = open;
        contest.ProblemPdf = new Blob { Data = [1, 2, 3] };
        contest.TestZip = new Blob { Data = [4, 5, 6] };
        await SaveAsync(contest);
        HttpClient client;
        if (path == "downloadtests")
        {
            var user = await factory.AddUserAsync();
            using var scope = factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var participant = AddCompetitor(contest, "Registered participant");
            participant.Contest = null!;
            participant.ContestId = contest.Id;
            context.Attach(user);
            participant.Members.Add(user);
            context.Add(participant);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            client = await factory.LoginAsync(user.UserName!);
        }
        else
        {
            client = factory.CreateSiteClient();
        }
        using var downloadClient = client;
        using var response = await client.GetAsync($"/contests/{contest.Id}/{path}", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        Assert.Equal(contentType, response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(contest.Abbr + suffix, response.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal(path == "downloadproblems" ? [1, 2, 3] : [4, 5, 6],
            await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, "/Account/Login")]
    [InlineData(true, "/Account/Manage/Email")]
    public async Task DownloadingTestsRequiresLoginAndEmailMigration(bool legacy, string redirectPath)
    {
        var contest = CreateContest();
        contest.IsOpen = true;
        contest.TestZip = new Blob { Data = [1, 2, 3] };
        await SaveAsync(contest);
        using var client = legacy
            ? await factory.LoginAsync((await factory.AddUserAsync(legacyLogin: true)).UserName!)
            : factory.CreateSiteClient();
        string path = $"/contests/{contest.Id}/downloadtests";
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = new Uri(client.BaseAddress!, response.Headers.Location!);
        Assert.Equal(redirectPath, location.AbsolutePath);
        Assert.Equal(path, QueryHelpers.ParseQuery(location.Query)["returnUrl"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Jury)]
    public async Task PublicRoutesNeverExposeHiddenContestsEvenToStaff(string? role)
    {
        var contest = CreateContest();
        contest.IsHidden = true;
        contest.IsOpen = true;
        contest.ProblemPdf = new Blob { Data = [1] };
        contest.TestZip = new Blob { Data = [2] };
        await SaveAsync(contest);
        using var client = role is null ? factory.CreateSiteClient() : await CreateStaffClientAsync(role);
        foreach (var route in new[] { "competitors", "standing", "downloadproblems", "downloadtests" })
        {
            using var response = await client.GetAsync($"/contests/{contest.Id}/{route}", TestContext.Current.CancellationToken);
            Assert.Equal(role is null && route == "downloadtests" ? HttpStatusCode.Redirect : HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("standing", "future")]
    [InlineData("downloadproblems", "future")]
    [InlineData("downloadtests", "future")]
    [InlineData("downloadtests", "closed")]
    [InlineData("downloadproblems", "missing")]
    [InlineData("downloadtests", "missing")]
    public async Task UnpublishedOrMissingMaterialsAreNotAccessible(string route, string state)
    {
        var contest = CreateContest();
        if (state == "future")
        {
            contest.StartTime = DateTimeOffset.UtcNow.AddDays(1);
            contest.EndTime = contest.StartTime.AddHours(5);
        }
        if (state != "missing")
        {
            contest.ProblemPdf = new Blob { Data = [1] };
            contest.TestZip = new Blob { Data = [2] };
        }
        await SaveAsync(contest);
        using var client = route == "downloadtests"
            ? await factory.LoginAsync((await factory.AddUserAsync()).UserName!)
            : factory.CreateSiteClient();
        using var response = await client.GetAsync($"/contests/{contest.Id}/{route}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<HttpClient> CreateStaffClientAsync(string role)
    {
        var user = await factory.AddUserAsync();
        using var scope = factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<int>>>();
        if (!await roles.RoleExistsAsync(role))
            Assert.True((await roles.CreateAsync(new IdentityRole<int>(role))).Succeeded);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(user.Id.ToString()))!, role)).Succeeded);
        return await factory.LoginAsync(user.UserName!);
    }

    private async Task SaveAsync(Contest contest)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        context.Add(contest);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Contest CreateContest() => new()
    {
        Name = "Public contest " + Guid.NewGuid().ToString("N"),
        Abbr = "acm" + Guid.NewGuid().ToString("N"),
        StartTime = DateTimeOffset.UtcNow.AddHours(-5),
        EndTime = DateTimeOffset.UtcNow.AddHours(-1),
        Messages = [],
        Problems = [],
        Competitors = []
    };

    private static Competitor AddCompetitor(Contest contest, string name)
    {
        var competitor = new Competitor
        {
            Name = name,
            Contest = contest,
            IsApproved = true,
            Members = [],
            Messages = [],
            Submissions = []
        };
        contest.Competitors.Add(competitor);
        return competitor;
    }

    private static Problem AddProblem(Contest contest)
    {
        var problem = new Problem
        {
            Number = 1,
            Name = "Problem one",
            Contest = contest,
            Checker = new Olymp.Domain.Models.Checker { Description = "Checker", Problems = [] },
            Messages = [],
            Submissions = [],
            Tests = []
        };
        contest.Problems.Add(problem);
        return problem;
    }

    private static Submission AddSubmission(Problem problem, User user, DateTimeOffset time, int status)
    {
        var submission = new Submission
        {
            Problem = problem,
            User = user,
            SourceCode = Encoding.UTF8.GetBytes("PRIVATE_SOURCE"),
            CommitTime = time,
            LastModification = time,
            StatusCode = status,
            Compilator = new Compilator
            {
                Language = "Test",
                Name = "Test",
                SourceExtension = ".txt",
                CommandLine = "test",
                ConfigName = "test",
                Submissions = []
            },
            CheckResults = []
        };
        problem.Submissions.Add(submission);
        return submission;
    }
}

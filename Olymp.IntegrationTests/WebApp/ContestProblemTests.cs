using System.Net;
using System.Security.Claims;
using System.Text;

using AngleSharp.Dom;
using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class ContestProblemTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task ContestWorkspaceShowsItsOrderedProblemsAndDefaultsToFirst(string culture)
    {
        var contest = Contest("Contest <em>name</em>", 2024);
        foreach (int number in Enumerable.Range(1, 60).Reverse())
            AddProblem(contest, number).Text = $"<p>Statement {number}</p>";
        var other = Contest("Other contest", 2025);
        AddProblem(other, 1).Name = "Unrelated problem";
        AddProblem(contest, 61).IsActive = false;
        await SeedAsync(contest, other);
        using var client = await LoginAsync(culture);
        using var document = await GetAsync(client, Path(contest));
        Assert.Equal(culture == "ru" ? $"Задачи – {contest.Name}" : $"Problems – {contest.Name}", document.Title);
        Assert.Equal(contest.Name, document.QuerySelector("main h1")!.TextContent);
        Assert.Null(document.QuerySelector("main h1 em"));
        Assert.Null(document.QuerySelector("header a[href='problems']"));
        Assert.Equal($"contests/{contest.Id}/problems", document.QuerySelector(".contest-header a[aria-current='page']")!.GetAttribute("href"));
        Assert.Equal(Enumerable.Range(1, 60).Select(number => $"{number}. Problem {number}"),
            document.QuerySelectorAll(".problem-list .list-group-item").Select(link => link.TextContent));
        Assert.Equal("1. Problem 1", document.QuerySelector("#problem-detail h2")!.TextContent);
        Assert.Equal("Statement 1", document.QuerySelector(".statement-html")!.TextContent);
        Assert.NotNull(document.QuerySelector("#submission-code"));
        Assert.NotNull(document.QuerySelector("#submissions"));
        Assert.Null(document.QuerySelector("#problem-year, #problem-contest, #problem-search, .pagination"));
        Assert.DoesNotContain("Unrelated problem", document.QuerySelector("main")!.TextContent);
        Assert.DoesNotContain("<!--Blazor:", document.DocumentElement.OuterHtml);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProblemProgressUsesOwnHistoryAndStandingAttemptRules(bool school, bool temporary)
    {
        var contest = Contest("Progress contest", 2024);
        contest.Abbr = school ? "school-progress" : "acm-progress";
        contest.IsOpen = false;
        contest.FreezeTime = contest.StartTime.AddMinutes(1);
        var problems = Enumerable.Range(1, 6).Select(number => AddProblem(contest, number)).ToArray();
        await SeedAsync(contest);
        var user = await factory.AddUserAsync();
        var other = await factory.AddUserAsync();
        using var client = temporary ? await CreateAccountClientAsync("temporary", contest)
            : await factory.LoginAsync(user.UserName!);
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var author = temporary
                ? await context.Users.SingleAsync(item => item is Competitor && ((Competitor)item).ContestId == contest.Id, TestContext.Current.CancellationToken)
                : await context.Users.SingleAsync(item => item.Id == user.Id, TestContext.Current.CancellationToken);
            var storedProblems = await context.Problems.Where(item => item.ContestId == contest.Id)
                .OrderBy(item => item.Number).ToListAsync(TestContext.Current.CancellationToken);
            var compiler = TestEntities.CreateCompilator();
            context.Compilators.Add(compiler);
            void Add(int index, int status, int minute, int? score = null, bool hidden = false, bool foreign = false)
            {
                var submission = TestEntities.CreateSubmission(author, storedProblems[index], compiler, contest.EndTime.AddMinutes(minute));
                submission.StatusCode = status;
                submission.Score = score;
                submission.IsHidden = hidden;
                if (foreign)
                {
                    submission.User = null!;
                    submission.UserId = other.Id;
                }
                context.Submissions.Add(submission);
            }
            Add(0, -1001, 1);
            Add(0, -1002, 2);
            Add(0, 2, 3, school ? 100 : null);
            Add(0, -1003, 4);
            Add(1, -1001, 1);
            Add(1, -1002, 2);
            Add(1, -1003, 3);
            Add(1, -1, 4);
            Add(1, 0, 5);
            Add(2, school ? -1003 : 2, 1, school ? 37 : null);
            if (school)
                Add(2, -1004, 2, 12);
            Add(3, 0, 1);
            Add(3, 1, 2);
            Add(3, -1, 3);
            Add(4, 2, 1, school ? 100 : null, foreign: true);
            Add(4, 2, 2, school ? 100 : null, hidden: true);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        string[] texts = school ? ["100/100", "0/100", "37/100", "", "", ""] : ["+2", "-3", "+", "", "", ""];
        string?[] colors = ["success", "danger", school ? "warning" : "success", null, null, null];
        foreach (string suffix in temporary ? new[] { "" } : ["", "&scope=all"])
        {
            using var document = await GetAsync(client, Path(contest, problems[0]) + suffix);
            var links = document.QuerySelectorAll(".problem-list .list-group-item");
            Assert.Equal(problems.Length, links.Length);
            Assert.Contains("active", links[0].ClassList);
            for (int index = 0; index < problems.Length; index++)
            {
                Assert.Equal(texts[index], links[index].QuerySelector(".text-nowrap")?.TextContent ?? "");
                if (colors[index] is string color)
                    Assert.Contains($"list-group-item-{color}", links[index].ClassList);
                else
                    Assert.DoesNotContain(links[index].ClassList, name => name is "list-group-item-success" or "list-group-item-danger" or "list-group-item-warning");
                Assert.Equal(problems[index].NameWithNumber + (texts[index].Length == 0 ? "" : $" ({texts[index]})"),
                    document.QuerySelector($"#workspace-problem option[value='{problems[index].Id}']")!.TextContent);
            }
        }
    }

    [Fact]
    public async Task SelectionLinksAndMobileFormKeepContestAndSelectedProblem()
    {
        var contest = Contest("Contest", 2024);
        var first = AddProblem(contest, 1);
        var second = AddProblem(contest, 2);
        second.Text = "<p>Second statement</p>";
        await SeedAsync(contest);
        using var client = await LoginAsync();
        using var document = await GetAsync(client, Path(contest));
        string link = document.QuerySelectorAll(".problem-list .list-group-item")[1].GetAttribute("href")!;
        Assert.Equal(Path(contest, second).TrimStart('/'), link);
        using var selected = await GetAsync(client, "/" + link);
        Assert.Equal($"{second.NameWithNumber} – {contest.Name}", selected.Title);
        Assert.Equal(second.NameWithNumber, selected.QuerySelector("#problem-detail h2")!.TextContent);
        Assert.Equal(second.NameWithNumber, Assert.Single(selected.QuerySelectorAll(".problem-list a[aria-current='page']")).TextContent);
        Assert.Equal(second.Id.ToString(), selected.QuerySelector("#workspace-problem option[selected]")!.GetAttribute("value"));
        var mobile = selected.QuerySelector(".problem-list form")!;
        Assert.Equal("get", mobile.GetAttribute("method"));
        Assert.Equal("problemId", mobile.QuerySelector("select")!.GetAttribute("name"));
        Assert.Equal(Path(contest, second).TrimStart('/'), selected.QuerySelector("#submission-code")!.Closest("form")!.GetAttribute("action"));
        foreach (var (page, problem) in new[] { (document, first), (selected, second) })
        {
            var baseUri = new Uri(client.BaseAddress!, page.QuerySelector("base")!.GetAttribute("href")!);
            var submitLink = page.QuerySelector("#problem-detail a[href$='#submit-heading']")!;
            Assert.Equal(new Uri(client.BaseAddress!, Path(contest, problem) + "#submit-heading"),
                new Uri(baseUri, submitLink.GetAttribute("href")!));
            Assert.NotNull(page.QuerySelector("#submit-heading"));
        }
        using var returned = await GetAsync(client, Path(contest, first));
        Assert.Equal($"{first.NameWithNumber} – {contest.Name}", returned.Title);
        Assert.Equal(first.NameWithNumber, returned.QuerySelector("#problem-detail h2")!.TextContent);
    }

    [Fact]
    public async Task EmptyContestHasNoEditorAndForeignOrMissingProblemDoesNotFallBack()
    {
        var empty = Contest("Empty", 2024);
        var other = Contest("Other", 2025);
        var problem = AddProblem(other, 1);
        await SeedAsync(empty, other);
        using var client = await LoginAsync();
        using var page = await GetAsync(client, Path(empty));
        Assert.Equal(empty.Name, page.QuerySelector("h1")!.TextContent);
        Assert.Null(page.QuerySelector("#submission-code"));
        foreach (string path in new[] { Path(empty, problem), Path(other) + "?problemId=2147483647", "/contests/2147483647/problems" })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("submission-code", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task StatementsPreserveLegacyHtmlAndOnlyExposeActiveOpenExamples(string culture)
    {
        var contest = Contest("Source contest", 2024);
        var problem = AddProblem(contest, 1);
        problem.Name = "Name <em>as text</em>";
        problem.Text = "<p><strong>Original HTML</strong> <a href='../legacy'>Legacy link</a></p>";
        AddTest(problem, 3, true, true, "  2\n3  \n");
        AddTest(problem, 1, true, true, "<script>input</script>\nПример");
        AddTest(problem, 2, false, true, "Private example");
        AddTest(problem, 4, true, false, "Inactive example");
        await SeedAsync(contest);
        using var client = await LoginAsync(culture);
        using var document = await GetAsync(client, Path(contest, problem));
        Assert.Equal("Original HTML", document.QuerySelector(".statement-html strong")!.TextContent);
        Assert.NotNull(document.QuerySelector(".statement-html a[href='../legacy']"));
        Assert.Null(document.QuerySelector("#problem-detail h2 em"));
        Assert.Equal([culture == "ru" ? "Стандартный ввод" : "Standard input", culture == "ru" ? "Стандартный вывод" : "Standard output"],
            document.QuerySelectorAll(".problem-examples th").Select(cell => cell.TextContent));
        var rows = document.QuerySelectorAll(".problem-examples tbody tr");
        Assert.Equal(2, rows.Length);
        Assert.Equal("<script>input</script>\nПример", rows[0].QuerySelector("samp")!.TextContent);
        Assert.Equal("  2\n3  \n", rows[1].QuerySelector("samp")!.TextContent);
        Assert.Null(document.QuerySelector(".problem-examples script"));
        Assert.DoesNotContain("Private example", document.DocumentElement.OuterHtml);
        Assert.DoesNotContain("Inactive example", document.DocumentElement.OuterHtml);
        Assert.DoesNotContain("<!--Blazor:", document.DocumentElement.OuterHtml);
    }

    [Theory]
    [InlineData("ru", true)]
    [InlineData("en", true)]
    [InlineData("ru", false)]
    [InlineData("en", false)]
    public async Task MissingHtmlUsesPdfOnlyWhenAvailable(string culture, bool hasPdf)
    {
        var contest = Contest("PDF contest", 2024);
        if (hasPdf)
            contest.ProblemPdf = new Blob { Data = "%PDF-test"u8.ToArray() };
        var problem = AddProblem(contest, 1);
        problem.Text = " \n ";
        await SeedAsync(contest);
        using var client = await LoginAsync(culture);
        foreach (string path in new[] { Path(contest, problem) })
        {
            using var document = await GetAsync(client, path);
            var pdf = document.QuerySelector(".problem-statement a");
            if (hasPdf)
            {
                Assert.NotNull(pdf);
                using var download = await client.GetAsync("/" + pdf.GetAttribute("href"), TestContext.Current.CancellationToken);
                download.EnsureSuccessStatusCode();
                Assert.Equal("application/pdf", download.Content.Headers.ContentType!.MediaType);
            }
            else
            {
                Assert.Null(pdf);
                Assert.Contains(culture == "ru" ? "Условие пока не опубликовано." : "The statement has not been published yet.",
                    document.QuerySelector(".problem-statement")!.TextContent);
            }
            Assert.Empty(document.QuerySelectorAll(".problem-examples"));
        }
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("future")]
    [InlineData("inactive")]
    [InlineData("missing")]
    public async Task UnpublishedProblemsAreNotAccessibleFromContestWorkspace(string reason)
    {
        var contest = Contest("Unavailable contest", 2024);
        var problem = AddProblem(contest, 1);
        problem.Text = "Private statement";
        switch (reason)
        {
            case "hidden": contest.IsHidden = true; break;
            case "closed": contest.IsOpen = false; break;
            case "ongoing":
                contest.StartTime = DateTimeOffset.UtcNow.AddHours(-1);
                contest.EndTime = DateTimeOffset.UtcNow.AddHours(1);
                break;
            case "future":
                contest.StartTime = DateTimeOffset.UtcNow.AddDays(1);
                contest.EndTime = DateTimeOffset.UtcNow.AddDays(2);
                break;
            case "inactive": problem.IsActive = false; break;
        }
        await SeedAsync(contest);
        if (reason == "missing")
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<OlympContext>().Problems.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }
        using var client = await LoginAsync();
        foreach (string path in new[] { Path(contest, problem) })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("Private statement", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("guest", "/Account/Login")]
    [InlineData("legacy", "/Account/Manage/Email")]
    public async Task ContestProblemsRequireSignInAndEmailMigration(string account, string redirect)
    {
        var contest = Contest("Authorized contest", 2024);
        var problem = AddProblem(contest, 1);
        await SeedAsync(contest);
        using var client = await CreateAccountClientAsync(account, contest);
        foreach (string path in new[] { Path(contest), Path(contest, problem) })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains(redirect, response.Headers.Location!.OriginalString);
        }
        if (account != "legacy")
        {
            using var home = await GetAsync(client, "/");
            Assert.Null(home.QuerySelector("header a[href='problems']"));
        }
    }

    private static string Path(Contest contest, Problem? problem = null) =>
        $"/contests/{contest.Id}/problems" + (problem is null ? "" : $"?problemId={problem.Id}");

    private async Task<HttpClient> CreateAccountClientAsync(string account, Contest contest)
    {
        if (account == "guest")
            return factory.CreateSiteClient();
        if (account == "legacy")
        {
            var legacy = await factory.AddUserAsync(legacyLogin: true);
            return await factory.LoginAsync(legacy.UserName!);
        }

        var competitor = TestEntities.CreateCompetitor(contest, $"temporary_{Guid.NewGuid():N}");
        competitor.Contest = null!;
        competitor.UserName = competitor.Name;
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.True((await users.CreateAsync(competitor, WebAppFactory.Password)).Succeeded);
        Assert.True((await users.AddClaimAsync(competitor, new Claim(ClaimNames.CompetitorContest, contest.Id.ToString()))).Succeeded);
        return await factory.LoginAsync(competitor.UserName);
    }

    private async Task<HttpClient> LoginAsync(string culture = "en")
    {
        var user = await factory.AddUserAsync();
        var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        return client;
    }

    private static async Task<IDocument> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
    }

    private async Task SeedAsync(params Contest[] contests)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await context.Submissions.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await context.Contests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        context.Contests.AddRange(contests);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Contest Contest(string name, int year)
    {
        var start = new DateTimeOffset(year, 6, 1, 10, 0, 0, TimeSpan.FromHours(7));
        var contest = TestEntities.CreateContest(name, start, start.AddHours(5));
        contest.IsOpen = true;
        return contest;
    }

    private static Problem AddProblem(Contest contest, int number)
    {
        var problem = TestEntities.CreateProblem(contest, number);
        contest.Problems.Add(problem);
        return problem;
    }

    private static void AddTest(Problem problem, int number, bool open, bool active, string input)
    {
        problem.Tests.Add(new Test
        {
            Problem = problem,
            Number = number,
            IsOpen = open,
            IsActive = active,
            Input = Encoding.UTF8.GetBytes(input),
            Output = Encoding.UTF8.GetBytes($"Output {number}\n")
        });
    }
}

using System.Net;
using System.Security.Claims;
using System.Text;

using AngleSharp.Dom;
using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Tests.Shared;
using Olymp.WebApp.Components.Submissions;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class PracticeSubmissionTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    private const string Source = "// Привет\nclass Program { static void Main() {} }\n";

    [Fact]
    public async Task SubmissionIsQueuedForPersonalAccountAndRedirectClearsCodeWithoutChangingContestResults()
    {
        var data = await SeedAsync();
        using var client = await factory.LoginAsync(data.User.UserName!);
        string path = ProblemPath(data.Contest.Id, data.Problem.Id);
        using var response = await PostAsync(client, path, data.Compiler.Id, Source);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains($"{path}#submission-", response.Headers.Location!.OriginalString);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var saved = await db.Submissions.Include(item => item.User).SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(data.User.Id, saved.UserId);
        Assert.Equal(data.Problem.Id, saved.ProblemId);
        Assert.Equal(data.Compiler.Id, saved.CompilatorId);
        Assert.Equal(Source, saved.Text);
        Assert.Equal(Encoding.GetEncoding(1251).GetBytes(Source), saved.SourceCode);
        Assert.Equal(0, saved.StatusCode);
        Assert.True(saved.CommitTime > data.Contest.EndTime);
        Assert.Equal(saved.CommitTime, saved.LastModification);
        using var page = await GetAsync(client, response.Headers.Location.OriginalString);
        Assert.Equal("", page.QuerySelector("#submission-code")!.TextContent);
        Assert.Equal(data.Compiler.Id.ToString(), page.QuerySelector("#submission-compiler option[selected]")!.GetAttribute("value"));
        Assert.Single(page.QuerySelectorAll("#submissions tbody tr"));
        Assert.Contains("Queued", page.QuerySelector("#submissions")!.TextContent);
        using var refresh = await client.GetAsync(response.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Equal(1, await db.Submissions.CountAsync(TestContext.Current.CancellationToken));

        // Even a later accepted result must not enter the original contest's standings.
        saved.StatusCode = 2;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var contest = await db.Contests.Include(item => item.Competitors).Include(item => item.Problems)
            .ThenInclude(item => item.Submissions).ThenInclude(item => item.User)
            .SingleAsync(item => item.Id == data.Contest.Id, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Standing.Create(contest, true, false, true, null).Results, result => result.User.Id == data.User.Id);
    }

    [Theory]
    [InlineData("short", "en", "between 10 and 32768")]
    [InlineData("long", "en", "between 10 and 32768")]
    [InlineData("unicode", "ru", "Windows-1251")]
    [InlineData("inactive", "ru", "компилятор недоступен")]
    [InlineData("missing", "en", "compiler is unavailable")]
    [InlineData("empty", "ru", "Введите исходный код")]
    public async Task InvalidSubmissionPreservesFieldsAndDoesNotCreateARecord(string reason, string culture, string error)
    {
        var data = await SeedAsync();
        using var client = await factory.LoginAsync(data.User.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        string code = reason switch
        {
            "short" => "tiny",
            "long" => new string('x', 32769),
            "unicode" => Source + "// 🙂",
            "empty" => "",
            _ => Source
        };
        int compilerId = reason == "missing" ? int.MaxValue : data.Compiler.Id;
        if (reason == "inactive")
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<OlympContext>().Compilators
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false), TestContext.Current.CancellationToken);
        }
        using var response = await PostAsync(client, ProblemPath(data.Contest.Id, data.Problem.Id), compilerId, code);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var page = await ParseAsync(response);
        Assert.Contains(error, page.QuerySelector("main")!.TextContent);
        Assert.Equal(code, page.QuerySelector("#submission-code")!.TextContent);
        if (reason is not "inactive" and not "missing")
            Assert.Equal(compilerId.ToString(), page.QuerySelector("#submission-compiler option[selected]")!.GetAttribute("value"));
        using var check = factory.Services.CreateScope();
        Assert.Empty(await check.ServiceProvider.GetRequiredService<OlympContext>().Submissions.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PersonalAndTeamHistoryIncludesContestSubmissionsAndProtectsSourceAndDiagnostics()
    {
        var data = await SeedAsync();
        var other = await factory.AddUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var team = await db.Competitors.Include(item => item.Members).SingleAsync(TestContext.Current.CancellationToken);
        var problem = await db.Problems.SingleAsync(TestContext.Current.CancellationToken);
        var compiler = await db.Compilators.SingleAsync(TestContext.Current.CancellationToken);
        var owner = await db.OlympUsers.SingleAsync(item => item.Id == data.User.Id, TestContext.Current.CancellationToken);
        var stranger = await db.OlympUsers.SingleAsync(item => item.Id == other.Id, TestContext.Current.CancellationToken);
        var personal = TestEntities.CreateSubmission(owner, problem, compiler, data.Contest.StartTime.AddMinutes(1));
        personal.Text = Source + "// <script>source</script>";
        personal.StatusCode = 2;
        var teamSubmission = TestEntities.CreateSubmission(team, problem, compiler, data.Contest.StartTime.AddMinutes(2));
        teamSubmission.Text = Source;
        teamSubmission.StatusCode = -1;
        teamSubmission.CheckResults.Add(new CheckResult
        {
            Submission = teamSubmission,
            CheckTime = DateTimeOffset.UtcNow,
            StatusCode = -1,
            Log = "Runner: private runner metadata\nCompilation:\nuser time: 1\ntotal time: 2\nCompiler error <script>diagnostics</script>"
        });
        var foreign = TestEntities.CreateSubmission(stranger, problem, compiler, DateTimeOffset.UtcNow);
        foreign.Text = "Private foreign source";
        db.Submissions.AddRange(personal, teamSubmission, foreign);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = await factory.LoginAsync(data.User.UserName!);
        using var page = await GetAsync(client, ProblemPath(data.Contest.Id, problem.Id));
        Assert.Equal([personal.Id.ToString()], page.QuerySelectorAll("#submissions tbody tr")
            .Select(row => row.Id!["submission-".Length..]));
        using var participantHistory = await GetAsync(client, ProblemPath(data.Contest.Id, problem.Id) + "&scope=participant");
        Assert.Equal([teamSubmission.Id.ToString()], participantHistory.QuerySelectorAll("#submissions tbody tr")
            .Select(row => row.Id!["submission-".Length..]));
        Assert.Contains(team.Name, participantHistory.QuerySelector("#submissions")!.TextContent);
        using var source = await GetAsync(client, $"/submissions/{personal.Id}");
        Assert.Equal(personal.Text, source.QuerySelector("main pre code")!.TextContent);
        Assert.Null(source.QuerySelector("main pre script"));
        Assert.Equal(ProblemPath(data.Contest.Id, problem.Id).TrimStart('/') + "#submissions",
            source.QuerySelector("main p a")!.GetAttribute("href"));
        using var download = await client.GetAsync($"/submissions/{teamSubmission.Id}/download", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("utf-8", download.Content.Headers.ContentType!.CharSet);
        Assert.Equal(Source, await download.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains($"{teamSubmission.Id}.cs", download.Content.Headers.ContentDisposition!.ToString());
        using var diagnostics = await GetAsync(client, $"/submissions/{teamSubmission.Id}");
        Assert.Contains("Compiler error <script>diagnostics</script>", diagnostics.QuerySelector("#compilation-errors")!.TextContent);
        Assert.Null(diagnostics.QuerySelector("#compilation-errors script"));
        Assert.DoesNotContain("private runner metadata", diagnostics.DocumentElement.OuterHtml);

        foreach (int id in new[] { foreign.Id, int.MaxValue })
            foreach (string path in new[] { $"/submissions/{id}", $"/submissions/{id}/download" })
            {
                using var denied = await client.GetAsync(path, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
                Assert.DoesNotContain("Private foreign source", await denied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }

        team.Members.Clear();
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var removed = await client.GetAsync($"/submissions/{teamSubmission.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
    }

    [Theory]
    [InlineData("participant")]
    [InlineData("coached")]
    public async Task RelatedScopesRequireVisibleSubmissionsMatchingCurrentFilters(string listScope)
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        User author = await db.Competitors.SingleAsync(TestContext.Current.CancellationToken);
        if (listScope == "coached")
        {
            var student = await factory.AddUserAsync();
            author = await db.Users.SingleAsync(item => item.Id == student.Id, TestContext.Current.CancellationToken);
            author.CoachId = data.User.Id;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = await factory.LoginAsync(data.User.UserName!);
        string selector = $"#submissions nav a[href*='scope={listScope}']";
        using var noHistory = await GetAsync(client, "/submissions");
        Assert.Null(noHistory.QuerySelector(selector));

        var contest = await db.Contests.SingleAsync(TestContext.Current.CancellationToken);
        var otherProblem = TestEntities.CreateProblem(contest, 2);
        var submission = TestEntities.CreateSubmission(author, otherProblem, data.Compiler, DateTimeOffset.UtcNow);
        submission.Compilator = null!;
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var general = await GetAsync(client, "/submissions");
        Assert.NotNull(general.QuerySelector(selector));
        using var matching = await GetAsync(client, ProblemPath(contest.Id, otherProblem.Id));
        Assert.NotNull(matching.QuerySelector(selector));
        using var empty = await GetAsync(client, ProblemPath(contest.Id, data.Problem.Id) + $"&scope={listScope}");
        Assert.Null(empty.QuerySelector(selector));
        Assert.Empty(empty.QuerySelectorAll("#submissions tbody tr"));

        otherProblem.IsActive = false;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var hiddenHistory = await GetAsync(client, "/submissions");
        Assert.Null(hiddenHistory.QuerySelector(selector));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllScopeExcludesHiddenAuthorsExceptViewerBeforePagination(bool participant)
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        User hiddenAuthor;
        if (participant)
            hiddenAuthor = await db.Competitors.SingleAsync(TestContext.Current.CancellationToken);
        else
        {
            var other = await factory.AddUserAsync();
            hiddenAuthor = await db.Users.SingleAsync(item => item.Id == other.Id, TestContext.Current.CancellationToken);
        }
        hiddenAuthor.IsHidden = true;
        hiddenAuthor.Name = "Hidden author";
        (await db.Users.SingleAsync(item => item.Id == data.User.Id, TestContext.Current.CancellationToken)).IsHidden = true;
        for (int i = 0; i < 21; i++)
        {
            var hidden = TestEntities.CreateSubmission(hiddenAuthor, data.Problem, data.Compiler, DateTimeOffset.UtcNow);
            hidden.User = null!;
            hidden.Problem = null!;
            hidden.Compilator = null!;
            db.Submissions.Add(hidden);
        }
        var visible = TestEntities.CreateSubmission(data.User, data.Problem, data.Compiler, data.Contest.StartTime);
        visible.User = null!;
        visible.Problem = null!;
        visible.Compilator = null!;
        db.Submissions.Add(visible);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var client = await factory.LoginAsync(data.User.UserName!);
        foreach (string path in new[] { "/submissions?scope=all", ProblemPath(data.Contest.Id, data.Problem.Id) + "&scope=all" })
        {
            using var page = await GetAsync(client, path);
            Assert.Equal($"submission-{visible.Id}", Assert.Single(page.QuerySelectorAll("#submissions tbody tr")).Id);
            Assert.DoesNotContain(hiddenAuthor.Name, page.QuerySelector("#submissions")!.TextContent);
            Assert.Null(page.QuerySelector("a[rel='next']"));
        }
    }

    [Fact]
    public async Task ListAppliesRelationshipsAndFreezeIndependentlyOfSelectedScope()
    {
        var data = await SeedAsync();
        var coached = await factory.AddUserAsync();
        var stranger = await factory.AddUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = await db.Contests.SingleAsync(TestContext.Current.CancellationToken);
        contest.IsOpen = false;
        contest.Abbr = "school-list";
        contest.FreezeTime = contest.StartTime.AddHours(2);
        (await db.Users.SingleAsync(item => item.Id == coached.Id, TestContext.Current.CancellationToken)).CoachId = data.User.Id;
        var team = await db.Competitors.SingleAsync(TestContext.Current.CancellationToken);

        Submission Add(User author, bool frozen)
        {
            var submission = TestEntities.CreateSubmission(author, data.Problem, data.Compiler,
                contest.FreezeTime.Value.AddMinutes(frozen ? 1 : -1));
            submission.User = null!;
            submission.Problem = null!;
            submission.Compilator = null!;
            submission.Text = "PRIVATE_SOURCE";
            submission.StatusCode = -1003;
            submission.Score = 37;
            db.Submissions.Add(submission);
            return submission;
        }

        var own = Add(data.User, true);
        var memberBefore = Add(team, false);
        var memberAfter = Add(team, true);
        var coachedBefore = Add(coached, false);
        var coachedAfter = Add(coached, true);
        var otherBefore = Add(stranger, false);
        var otherAfter = Add(stranger, true);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = await factory.LoginAsync(data.User.UserName!);
        string path = ProblemPath(contest.Id, data.Problem.Id);
        using var page = await GetAsync(client, path + "&scope=all");
        foreach (var submission in new[] { own, memberBefore, coachedBefore })
        {
            var cells = page.QuerySelector($"#submission-{submission.Id}")!.QuerySelectorAll("td");
            Assert.Equal("Wrong answer on test 3", cells[3].TextContent.Trim());
            Assert.Equal("37", cells[4].TextContent);
        }
        foreach (var submission in new[] { memberAfter, coachedAfter, otherAfter })
        {
            var cells = page.QuerySelector($"#submission-{submission.Id}")!.QuerySelectorAll("td");
            Assert.Equal("Submitted", cells[3].TextContent.Trim());
            Assert.Equal("", cells[4].TextContent);
        }
        var otherCells = page.QuerySelector($"#submission-{otherBefore.Id}")!.QuerySelectorAll("td");
        Assert.Equal("Not accepted", otherCells[3].TextContent.Trim());
        Assert.Equal("", otherCells[4].TextContent);
        Assert.Single(page.QuerySelectorAll("#submissions a[href$='/download']"));
        Assert.DoesNotContain("PRIVATE_SOURCE", page.DocumentElement.OuterHtml);
        using var participants = await GetAsync(client, path + "&scope=participant");
        Assert.Equal(2, participants.QuerySelectorAll("#submissions tbody tr").Length);
        using var students = await GetAsync(client, path + "&scope=coached");
        Assert.Equal(2, students.QuerySelectorAll("#submissions tbody tr").Length);
    }

    [Fact]
    public async Task DirectCoachRelationshipAllowsPersonalSourceWithoutCoachClaimInOpenContest()
    {
        var data = await SeedAsync();
        var student = await factory.AddUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        (await db.Users.SingleAsync(item => item.Id == student.Id, TestContext.Current.CancellationToken)).CoachId = data.User.Id;
        var submission = TestEntities.CreateSubmission(student, data.Problem, data.Compiler, DateTimeOffset.UtcNow);
        submission.User = null!;
        submission.Problem = null!;
        submission.Compilator = null!;
        submission.Text = Source;
        submission.StatusCode = -1;
        submission.CheckResults.Add(new CheckResult
        {
            Submission = submission, StatusCode = -1, CheckTime = DateTimeOffset.UtcNow, Log = "Compiler diagnostics"
        });
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = await factory.LoginAsync(data.User.UserName!);
        using var list = await GetAsync(client, $"/submissions?scope=coached&contestId={data.Contest.Id}");
        Assert.NotNull(list.QuerySelector($"a[href='submissions/{submission.Id}/download']"));
        using var details = await GetAsync(client, $"/submissions/{submission.Id}");
        Assert.Equal(Source, details.QuerySelector("pre code")!.TextContent);
        Assert.Contains("Compiler diagnostics", details.QuerySelector("#compilation-errors")!.TextContent);
        using var download = await client.GetAsync($"/submissions/{submission.Id}/download", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Source, await download.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await db.Contests.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsOpen, false), TestContext.Current.CancellationToken);
        using var denied = await client.GetAsync($"/submissions/{submission.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Fact]
    public async Task AllScopeHasTwentyRowsAndTenPagesWhileOwnScopeContinues()
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 205; i++)
        {
            var submission = TestEntities.CreateSubmission(data.User, data.Problem, data.Compiler, now);
            submission.User = null!;
            submission.Problem = null!;
            submission.Compilator = null!;
            db.Submissions.Add(submission);
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = await factory.LoginAsync(data.User.UserName!);
        string path = $"/submissions?contestId={data.Contest.Id}";
        using var first = await GetAsync(client, path + "&scope=all");
        Assert.Equal(20, first.QuerySelectorAll("#submissions tbody tr").Length);
        var ids = first.QuerySelectorAll("#submissions tbody tr").Select(row => int.Parse(row.Id!["submission-".Length..])).ToArray();
        Assert.Equal(ids.OrderDescending(), ids);
        using var tenth = await GetAsync(client, path + "&scope=all&page=10");
        Assert.Equal(20, tenth.QuerySelectorAll("#submissions tbody tr").Length);
        Assert.Null(tenth.QuerySelector("a[rel='next']"));
        Assert.Equal(["1", "7", "8", "9"], tenth.QuerySelectorAll(".pagination a").Select(link => link.TextContent));
        Assert.Equal("10", tenth.QuerySelector(".pagination [aria-current='page']")!.TextContent);
        using var denied = await client.GetAsync(path + "&scope=all&page=11", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var own = await GetAsync(client, path + "&page=11");
        Assert.Equal(5, own.QuerySelectorAll("#submissions tbody tr").Length);
    }

    [Fact]
    public async Task CompilerDefaultsToLatestActiveChoiceAcrossTheContest()
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = await db.Contests.SingleAsync(TestContext.Current.CancellationToken);
        var problem = TestEntities.CreateProblem(contest, 2);
        var author = await db.OlympUsers.SingleAsync(item => item.Id == data.User.Id, TestContext.Current.CancellationToken);
        var active = TestEntities.CreateCompilator();
        active.Description = "Latest active choice";
        var inactive = TestEntities.CreateCompilator();
        inactive.Description = "Inactive choice";
        inactive.IsActive = false;
        db.Submissions.AddRange(
            TestEntities.CreateSubmission(author, problem, active, DateTimeOffset.UtcNow.AddMinutes(-1)),
            TestEntities.CreateSubmission(author, problem, inactive, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = await factory.LoginAsync(data.User.UserName!);
        using var page = await GetAsync(client, ProblemPath(data.Contest.Id, data.Problem.Id));
        Assert.Equal(active.Id.ToString(), page.QuerySelector("#submission-compiler option[selected]")!.GetAttribute("value"));
        Assert.DoesNotContain(page.QuerySelectorAll("#submission-compiler option"), option => option.GetAttribute("value") == inactive.Id.ToString());
        Assert.Empty(page.QuerySelectorAll("#submissions tbody tr"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task SubmissionStatusesUseEnglishForBothCultures(string culture)
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        foreach (int status in new[] { 0, 1, 2, -1003 })
        {
            var submission = TestEntities.CreateSubmission(data.User, data.Problem, data.Compiler, DateTimeOffset.UtcNow);
            submission.User = null!;
            submission.Problem = null!;
            submission.Compilator = null!;
            submission.StatusCode = status;
            submission.Score = status == 2 ? 100 : null;
            db.Submissions.Add(submission);
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = await factory.LoginAsync(data.User.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var page = await GetAsync(client, ProblemPath(data.Contest.Id, data.Problem.Id));
        string history = page.QuerySelector("#submissions")!.TextContent;
        foreach (string status in new[] { "Queued...", "Checking...", "Accepted", "Wrong answer on test 3" })
            Assert.Contains(status, history);
        Assert.Equal(5, page.QuerySelectorAll("#submissions thead th").Length);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, 100)]
    [InlineData(true, null)]
    [InlineData(true, 0)]
    public async Task ScoreColumnDependsOnContestTypeRatherThanSubmissionScores(bool isSchool, int? score)
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Contests.ExecuteUpdateAsync(setters => setters.SetProperty(contest => contest.Abbr,
            isSchool ? "school-test" : "acm-test"), TestContext.Current.CancellationToken);
        var submission = TestEntities.CreateSubmission(data.User, data.Problem, data.Compiler, DateTimeOffset.UtcNow);
        submission.User = null!;
        submission.Problem = null!;
        submission.Compilator = null!;
        submission.Score = score;
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = await factory.LoginAsync(data.User.UserName!);
        using var page = await GetAsync(client, ProblemPath(data.Contest.Id, data.Problem.Id));
        Assert.Equal(isSchool ? 6 : 5, page.QuerySelectorAll("#submissions thead th").Length);
        Assert.Equal(isSchool ? 6 : 5, page.QuerySelectorAll("#submissions tbody td").Length);
        Assert.Equal(isSchool, page.QuerySelectorAll("#submissions thead th").Any(header => header.TextContent == "Points"));
        Assert.NotNull(page.QuerySelector("a[href='rules'][target='_blank']"));
        Assert.Null(page.QuerySelector("#submission-code-hint"));
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("closed")]
    [InlineData("future")]
    [InlineData("inactive")]
    [InlineData("disqualified")]
    public async Task PostRechecksAccessAfterTheFormWasOpened(string reason)
    {
        var data = await SeedAsync();
        using var client = await factory.LoginAsync(data.User.UserName!);
        string path = ProblemPath(data.Contest.Id, data.Problem.Id);
        using var initial = await GetAsync(client, path);
        var fields = Fields(initial, data.Compiler.Id, Source);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        if (reason == "disqualified")
            await db.OlympUsers.Where(item => item.Id == data.User.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsDisqualified, true), TestContext.Current.CancellationToken);
        else if (reason == "inactive")
            await db.Problems.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false), TestContext.Current.CancellationToken);
        else if (reason == "hidden")
            await db.Contests.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsHidden, true), TestContext.Current.CancellationToken);
        else if (reason == "closed")
            await db.Contests.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsOpen, false), TestContext.Current.CancellationToken);
        else
            await db.Contests.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.StartTime, DateTimeOffset.UtcNow.AddHours(1))
                .SetProperty(item => item.EndTime, DateTimeOffset.UtcNow.AddHours(5)), TestContext.Current.CancellationToken);
        using var content = new FormUrlEncodedContent(fields);
        using var response = await client.PostAsync(path, content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var rejected = await ParseAsync(response);
        Assert.Null(rejected.QuerySelector("#submission-code"));
        Assert.Empty(await db.Submissions.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("ru", "Вам недоступна отправка решений.")]
    [InlineData("en", "You are not allowed to submit solutions.")]
    public async Task RejectedSubmissionHandlerPreservesFieldsAndDoesNotCreateARecord(string culture, string error)
    {
        var data = await SeedAsync();
        await using var site = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IAuthorizationHandler, RejectRepeatedSubmissionHandler>()));
        using var client = site.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var login = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new()
        {
            ["Input.Login"] = data.User.UserName!,
            ["Input.Password"] = WebAppFactory.Password
        });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        using var response = await PostAsync(client, ProblemPath(data.Contest.Id, data.Problem.Id), data.Compiler.Id, Source);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var page = await ParseAsync(response);
        Assert.Contains(error, page.QuerySelector("form:has(#submission-code)")!.ParentElement!.QuerySelector("[role='alert']")!.TextContent);
        Assert.Equal(Source, page.QuerySelector("#submission-code")!.TextContent);
        Assert.Equal(data.Compiler.Id.ToString(), page.QuerySelector("#submission-compiler option[selected]")!.GetAttribute("value"));
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OlympContext>().Submissions.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PostingProblemFromAnotherContestDoesNotSaveSubmission()
    {
        var data = await SeedAsync();
        using var client = await factory.LoginAsync(data.User.UserName!);
        using var initial = await GetAsync(client, ProblemPath(data.Contest.Id, data.Problem.Id));
        var fields = Fields(initial, data.Compiler.Id, Source);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var other = TestEntities.CreateContest("Other", data.Contest.StartTime, data.Contest.EndTime);
        other.IsOpen = true;
        other.Problems.Add(TestEntities.CreateProblem(other));
        db.Contests.Add(other);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        string wrongPath = ProblemPath(other.Id, data.Problem.Id);
        using var page = await client.GetAsync(wrongPath, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        using var content = new FormUrlEncodedContent(fields);
        using var posted = await client.PostAsync(wrongPath, content, TestContext.Current.CancellationToken);
        Assert.False(posted.IsSuccessStatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, posted.StatusCode);
        Assert.Empty(await db.Submissions.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CrossSitePostIsRejectedAndNoCompilersDisablesTheForm()
    {
        var data = await SeedAsync();
        using var client = await factory.LoginAsync(data.User.UserName!);
        string path = ProblemPath(data.Contest.Id, data.Problem.Id);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "submit-solution",
            ["Input.CompilatorId"] = data.Compiler.Id.ToString(),
            ["Input.Code"] = Source
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Headers.Add("Origin", "https://other.example.test");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Compilators.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false), TestContext.Current.CancellationToken);
        using var page = await GetAsync(client, path);
        Assert.Contains("no active compilers", page.QuerySelector("main")!.TextContent);
        Assert.NotNull(page.QuerySelector("fieldset[disabled]"));
        Assert.Empty(await db.Submissions.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("guest", "/Account/Login")]
    [InlineData("legacy", "/Account/Manage/Email")]
    public async Task SourceAndDownloadRequireSignInAndEmailMigration(string account, string redirect)
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var submission = TestEntities.CreateSubmission(data.User, data.Problem, data.Compiler, DateTimeOffset.UtcNow);
        submission.User = null!;
        submission.Problem = null!;
        submission.Compilator = null!;
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = account switch
        {
            "guest" => factory.CreateSiteClient(),
            "legacy" => await factory.LoginAsync((await factory.AddUserAsync(legacyLogin: true)).UserName!),
            _ => await TemporaryClientAsync(data.Contest)
        };
        foreach (string path in new[] { ProblemPath(data.Contest.Id, data.Problem.Id), $"/submissions/{submission.Id}", $"/submissions/{submission.Id}/download" })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains(redirect, response.Headers.Location!.OriginalString);
        }
    }

    private sealed class RejectRepeatedSubmissionHandler(IHttpContextAccessor http)
        : AuthorizationHandler<SubmitSolutionRequirement, Problem>
    {
        private int checks;

        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
            SubmitSolutionRequirement requirement, Problem resource)
        {
            // Allow POST initialization to render the form, then deny the handler's recheck.
            if (HttpMethods.IsPost(http.HttpContext!.Request.Method) && ++checks > 1)
                context.Fail();
            return Task.CompletedTask;
        }
    }

    private async Task<HttpClient> TemporaryClientAsync(Contest contest)
    {
        using var scope = factory.Services.CreateScope();
        var competitor = TestEntities.CreateCompetitor(contest, $"temp_{Guid.NewGuid():N}");
        competitor.Contest = null!;
        competitor.UserName = competitor.Name;
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.True((await users.CreateAsync(competitor, WebAppFactory.Password)).Succeeded);
        Assert.True((await users.AddClaimAsync(competitor, new Claim(ClaimNames.CompetitorContest, contest.Id.ToString()))).Succeeded);
        return await factory.LoginAsync(competitor.UserName);
    }

    private async Task<(OlympUser User, Contest Contest, Problem Problem, Compilator Compiler)> SeedAsync()
    {
        var user = await factory.AddUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.CheckResults.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await db.Submissions.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await db.Set<Dictionary<string, object>>("Participations").ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await db.Contests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await db.Compilators.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        var contest = TestEntities.CreateContest("Practice contest", new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 6, 1, 15, 0, 0, TimeSpan.Zero));
        contest.IsOpen = true;
        var problem = TestEntities.CreateProblem(contest);
        contest.Problems.Add(problem);
        var member = await db.OlympUsers.SingleAsync(item => item.Id == user.Id, TestContext.Current.CancellationToken);
        var team = TestEntities.CreateCompetitor(contest, "User's team");
        team.Members.Add(member);
        contest.Competitors.Add(team);
        var compiler = TestEntities.CreateCompilator();
        db.AddRange(contest, compiler);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (user, contest, problem, compiler);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, int compilerId, string code)
    {
        using var page = await GetAsync(client, path);
        using var content = new FormUrlEncodedContent(Fields(page, compilerId, code));
        return await client.PostAsync(path, content, TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, string> Fields(IDocument page, int compilerId, string code)
    {
        var form = page.QuerySelector("form:has(#submission-code)")!;
        var fields = form.QuerySelectorAll("input[type='hidden'][name]").ToDictionary(input => input.GetAttribute("name")!, input => input.GetAttribute("value") ?? "");
        fields["Input.CompilatorId"] = compilerId.ToString();
        fields["Input.Code"] = code;
        return fields;
    }

    private static string ProblemPath(int contestId, int problemId) => $"/contests/{contestId}/problems?problemId={problemId}";

    private static async Task<IDocument> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await ParseAsync(response);
    }

    private static async Task<IDocument> ParseAsync(HttpResponseMessage response) =>
        await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
}

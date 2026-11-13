using System.Reflection;
using System.Security.Claims;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Checker;

using Xunit;

using JuryMessages = Olymp.WebApp.Components.Pages.Staff.Jury.Messages;
using JuryRunners = Olymp.WebApp.Components.Pages.Staff.Jury.Runners;
using JuryStanding = Olymp.WebApp.Components.Pages.Staff.Jury.Standing;
using JurySubmissions = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionList;

namespace Olymp.IntegrationTests.WebApp;

public partial class StaffListTests
{
    private static ClaimsPrincipal Staff(int id, string role = RoleNames.Admin) => new(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, id.ToString()), new(ClaimTypes.Role, role)], "Test"));

    private static Task ActAsync(object page, string method, params object?[] args) =>
        (Task)page.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args)!;

    private static void SetEditor(JuryMessages page, string text, int? problemId = null)
    {
        var editor = typeof(JuryMessages).GetField("_editor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        editor.GetType().GetProperty("Text")!.SetValue(editor, text);
        editor.GetType().GetProperty("ProblemId")!.SetValue(editor, problemId);
    }

    [Fact]
    public async Task GlobalSubmissionListFiltersContestsAndPreservesReturnContext()
    {
        var author = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var first = TestEntities.CreateContest("First global", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(5));
        var second = TestEntities.CreateContest("Second global", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(5));
        first.IsHidden = second.IsHidden = true;
        var compiler = TestEntities.CreateCompilator();
        var submissions = new[] { first, second }.Select(contest =>
        {
            var submission = TestEntities.CreateSubmission(author, TestEntities.CreateProblem(contest), compiler, DateTimeOffset.Now);
            submission.User = null!;
            return submission;
        }).ToArray();
        db.Submissions.AddRange(submissions);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var all = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>(
            $"/jury/submissions?AuthorId={author.Id}", [], Staff(author.Id, RoleNames.Jury)));
        Assert.Equal(2, all.QuerySelectorAll("tbody tr").Length);
        Assert.Contains(first.Name, all.QuerySelector("tbody")!.TextContent);
        Assert.Contains(second.Name, all.QuerySelector("tbody")!.TextContent);
        using var filtered = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>(
            $"/jury/submissions?AuthorId={author.Id}&Contest={second.Id}", [], Staff(author.Id)));
        Assert.Single(filtered.QuerySelectorAll("tbody tr"));
        string sourceUrl = filtered.QuerySelector("tbody tr td a")!.GetAttribute("href")!;
        Assert.Contains("returnUrl=", sourceUrl);
        Assert.Contains($"jury/contests/{second.Id}/problems", filtered.QuerySelector("tbody")!.InnerHtml);
        using var source = await new HtmlParser().ParseDocumentAsync(await RenderAsync<Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionDetails>(
            sourceUrl, new() { ["Id"] = submissions[1].Id }, Staff(author.Id)));
        Assert.Contains(source.QuerySelectorAll("a"), link => link.GetAttribute("href") == $"/jury/submissions?AuthorId={author.Id}&Contest={second.Id}");
    }

    [Fact]
    public async Task FilteredRetestCoversAllPagesWarnsAgainForNewAcceptedResultsAndRetainsHistory()
    {
        var author = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Retest selection", DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(4));
        var problem = TestEntities.CreateProblem(contest);
        var inactive = TestEntities.CreateProblem(contest, 2);
        inactive.IsActive = false;
        var compiler = TestEntities.CreateCompilator();
        var submissions = Enumerable.Range(0, 25).Select(index =>
        {
            var submission = TestEntities.CreateSubmission(author, index == 24 ? inactive : problem, compiler, DateTimeOffset.Now);
            submission.User = null!;
            submission.StatusCode = index switch { 21 => 2, 22 => 0, 23 => 1, _ => -2001 };
            submission.Score = 37;
            submission.ScoresByTest = "37";
            return submission;
        }).ToArray();
        submissions[0].CheckResults.Add(new CheckResult { Submission = submissions[0], CheckTime = DateTimeOffset.UtcNow, StatusCode = -2001, Log = "Keep history" });
        db.Submissions.AddRange(submissions);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        string url = $"/jury/submissions?Contest={contest.Id}&AuthorId={author.Id}&Compiler={compiler.Id}";
        using var preview = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>(url, [], Staff(author.Id),
            page => ActAsync(page, "PrepareRetestAsync")));
        Assert.Equal(20, preview.QuerySelectorAll("tbody tr").Length);
        Assert.Contains(string.Format(SiteResources.StaffRetestConfirm, 22), preview.Body!.TextContent);
        Assert.Contains(string.Format(SiteResources.StaffRetestAcceptedWarning, 1), preview.Body!.TextContent);

        using var result = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>(url, [], Staff(author.Id), async page =>
        {
            await ActAsync(page, "PrepareRetestAsync");
            await db.Submissions.Where(item => item.Id == submissions[22].Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.StatusCode, 2), TestContext.Current.CancellationToken);
            await ActAsync(page, "RetestAsync");
            Assert.Equal(2, await db.Submissions.Where(item => item.Id == submissions[21].Id).Select(item => item.StatusCode).SingleAsync(TestContext.Current.CancellationToken));
            Assert.NotNull(typeof(JurySubmissions).GetField("_retestTotal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page));
            await ActAsync(page, "RetestAsync");
        }));
        Assert.Contains(string.Format(SiteResources.StaffRetestQueued, 23), result.Body!.TextContent);
        db.ChangeTracker.Clear();
        var saved = await db.Submissions.Where(item => item.ProblemId == problem.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(23, saved.Count(item => item.StatusCode == 0 && item.Score is null && item.ScoresByTest is null));
        Assert.Equal(1, saved.Single(item => item.Id == submissions[23].Id).StatusCode);
        Assert.Equal(-2001, await db.Submissions.Where(item => item.Id == submissions[24].Id).Select(item => item.StatusCode).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Keep history", await db.CheckResults.Where(item => item.SubmissionId == submissions[0].Id).Select(item => item.Log).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task JuryCannotRetestByInvokingTheHandler()
    {
        var author = await factory.AddUserAsync();
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>("/jury/submissions", [], Staff(author.Id, RoleNames.Jury),
            async component => { await ActAsync(component, "PrepareRetestAsync"); await ActAsync(component, "RetestAsync"); }));
        Assert.DoesNotContain(page.QuerySelectorAll("button"), button => button.TextContent == SiteResources.StaffRetestFiltered);
    }

    [Fact]
    public async Task MessageAuthorSelectionShowsAccountAndResetPreservesOtherFilters()
    {
        var author = await factory.AddUserAsync();
        var other = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Author filter", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(4));
        var problem = TestEntities.CreateProblem(contest);
        db.Problems.Add(problem);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.Messages.AddRange(
            new Message { Contest = null!, ContestId = contest.Id, ProblemId = problem.Id, UserId = author.Id, UserText = "Selected question" },
            new Message { Contest = null!, ContestId = contest.Id, ProblemId = problem.Id, UserId = other.Id, UserText = "Other question" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        string path = $"/jury/contests/{contest.Id}/messages?AuthorId={author.Id}&Author=unrelated&Problem={problem.Id}&Answered=false";
        using var selected = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryMessages>(
            path, new() { ["ContestId"] = contest.Id }, Staff(author.Id)));
        var field = selected.QuerySelector("#message-author")!;
        Assert.True(field.HasAttribute("readonly"));
        Assert.Contains(author.Name, field.GetAttribute("value"));
        Assert.Contains(author.UserName!, field.GetAttribute("value"));
        Assert.Contains($"ID {author.Id}", field.GetAttribute("value"));
        Assert.Contains("Selected question", selected.Body!.TextContent);
        Assert.DoesNotContain("Other question", selected.Body.TextContent);
        string reset = selected.QuerySelector("#message-author + a")!.GetAttribute("href")!;
        Assert.DoesNotContain("Author", reset);
        Assert.Contains($"Problem={problem.Id}", reset);
        Assert.Contains("Answered=false", reset, StringComparison.OrdinalIgnoreCase);
        using var cleared = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryMessages>(
            reset, new() { ["ContestId"] = contest.Id }, Staff(author.Id)));
        Assert.False(cleared.QuerySelector("#message-author")!.HasAttribute("readonly"));
        Assert.Contains("Selected question", cleared.Body!.TextContent);
        Assert.Contains("Other question", cleared.Body.TextContent);
    }

    [Fact]
    public async Task MessageCrudPreservesQuestionsAndDetectsConcurrentEdits()
    {
        var author = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Messages", DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(4));
        var problem = TestEntities.CreateProblem(contest);
        db.Problems.Add(problem);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var question = new Message { Contest = null!, ContestId = contest.Id, UserId = author.Id, UserText = "Question preserved", SendTime = DateTimeOffset.UtcNow };
        db.Messages.Add(question);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        string announcementText = $"Announcement {Guid.NewGuid():N}";
        await RenderAsync<JuryMessages>($"/jury/contests/{contest.Id}/messages", new() { ["ContestId"] = contest.Id }, Staff(author.Id), async page =>
        {
            typeof(JuryMessages).GetMethod("NewAnnouncement", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
            SetEditor(page, announcementText, problem.Id);
            await ActAsync(page, "SaveAsync");
            var announcement = await db.Messages.SingleAsync(item => item.JuryText == announcementText, TestContext.Current.CancellationToken);
            Assert.Null(announcement.UserId);
            Assert.Equal(problem.Id, announcement.ProblemId);
            typeof(JuryMessages).GetMethod("Edit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [question]);
            SetEditor(page, "<b>First reply</b>");
            await ActAsync(page, "SaveAsync");
            var answered = await db.Messages.AsNoTracking().SingleAsync(item => item.Id == question.Id, TestContext.Current.CancellationToken);
            Assert.Equal("<b>First reply</b>", answered.JuryText);
            typeof(JuryMessages).GetMethod("Edit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [answered]);
            SetEditor(page, "My unsaved reply");
            await db.Messages.Where(item => item.Id == question.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.JuryText, "Another jury reply"), TestContext.Current.CancellationToken);
            await ActAsync(page, "SaveAsync");
            Assert.Equal("Another jury reply", await db.Messages.Where(item => item.Id == question.Id).Select(item => item.JuryText).SingleAsync(TestContext.Current.CancellationToken));
            var current = await db.Messages.AsNoTracking().SingleAsync(item => item.Id == question.Id, TestContext.Current.CancellationToken);
            typeof(JuryMessages).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, current);
            await ActAsync(page, "DeleteAsync");
            var cleared = await db.Messages.AsNoTracking().SingleAsync(item => item.Id == question.Id, TestContext.Current.CancellationToken);
            Assert.Null(cleared.JuryText);
            Assert.Equal(question.UserText, cleared.UserText);
            Assert.Equal(question.SendTime, cleared.SendTime);
            typeof(JuryMessages).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, announcement);
            await ActAsync(page, "DeleteAsync");
            Assert.False(await db.Messages.AnyAsync(item => item.Id == announcement.Id, TestContext.Current.CancellationToken));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MessageChangesRequireStoredContestAssignment(bool assigned)
    {
        var author = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Message access", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(5));
        db.Contests.Add(contest);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        if (assigned)
        {
            db.UserClaims.Add(new IdentityUserClaim<int> { UserId = author.Id, ClaimType = ClaimNames.ContestManage, ClaimValue = contest.Id.ToString() });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        string marker = Guid.NewGuid().ToString("N");
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryMessages>(
            $"/jury/contests/{contest.Id}/messages?Announcements=true", new() { ["ContestId"] = contest.Id }, Staff(author.Id, RoleNames.Jury), async page =>
            {
                typeof(JuryMessages).GetMethod("NewAnnouncement", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
                SetEditor(page, marker);
                await ActAsync(page, "SaveAsync");
            }));
        Assert.Equal(assigned, await db.Messages.AnyAsync(item => item.ContestId == contest.Id && item.JuryText == marker, TestContext.Current.CancellationToken));
        if (!assigned)
            Assert.Contains(SiteResources.StaffAccessDenied, html.Body!.TextContent);
    }

    [Fact]
    public async Task JuryStandingShowsHiddenFrozenContestWithoutChangingPublicAccess()
    {
        var author = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Frozen jury", DateTimeOffset.Now.AddHours(-2), DateTimeOffset.Now.AddHours(3));
        contest.IsHidden = true;
        contest.FreezeTime = DateTimeOffset.Now.AddHours(-1);
        var participant = TestEntities.CreateCompetitor(contest, "Jury standing participant");
        participant.IsApproved = true;
        var problem = TestEntities.CreateProblem(contest);
        var submission = TestEntities.CreateSubmission(participant, problem, TestEntities.CreateCompilator(), DateTimeOffset.Now);
        submission.StatusCode = 2;
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryStanding>(
            $"/jury/contests/{contest.Id}/standing", new() { ["ContestId"] = contest.Id }, Staff(author.Id, RoleNames.Jury)));
        Assert.Contains(participant.Name, html.QuerySelector("tbody")!.TextContent);
        Assert.Contains("+", html.QuerySelector("tbody")!.TextContent);
        Assert.DoesNotContain(SiteResources.FrozenAt, html.Body!.TextContent);
        using var client = await factory.LoginAsync(author.UserName!);
        using var publicPage = await client.GetAsync($"/contests/{contest.Id}/standing", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, publicPage.StatusCode);
    }
    [Fact]
    public async Task CompilerAvailabilityRequiresPassedSelfTestsAndMatchingVersion()
    {
        var runner = new Olymp.IntegrationTests.Services.Checkers.CheckerTestData.TestRunner();
        var checker = await Olymp.IntegrationTests.Services.Checkers.CheckerTestData.CreateCheckerAsync(runner, "available-compiler");
        var compiler = TestEntities.CreateCompilator();
        compiler.Name = "available-compiler";
        compiler.Description = "Test compiler";
        Assert.False(checker.SupportsCompiler(compiler));
        Assert.True(await checker.RunSelfTests(TestContext.Current.CancellationToken));
        Assert.True(checker.SupportsCompiler(compiler));
        compiler.Description = "Another version";
        Assert.False(checker.SupportsCompiler(compiler));
        compiler.Description = "Test compiler";
        compiler.Name = "another-compiler";
        Assert.False(checker.SupportsCompiler(compiler));
    }

    [Fact]
    public async Task RunnerViewRetainsDiagnosticsAndTrialRunKeepsSource()
    {
        var author = await factory.AddUserAsync();
        var runner = new Olymp.IntegrationTests.Services.Checkers.CheckerTestData.TestRunner { FailureTests = [1] };
        var checker = await Olymp.IntegrationTests.Services.Checkers.CheckerTestData.CreateCheckerAsync(runner, "compiler-details");
        await checker.RunSelfTests(TestContext.Current.CancellationToken);
        var manager = new Olymp.IntegrationTests.Services.Checkers.CheckerTestData.PageManager(checker);
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryRunners>(
            $"/jury/runners?Runner={runner.Id}", [], Staff(author.Id, RoleNames.Jury), async page =>
            {
                var input = typeof(JuryRunners).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
                input.GetType().GetProperty("Code")!.SetValue(input, "int main(void) { return 0; }");
                input.GetType().GetProperty("Input")!.SetValue(input, "custom input");
                await ActAsync(page, "RunAsync");
                await ActAsync(page, "SetActiveAsync", int.MaxValue, true);
            }, services => { services.RemoveAll<ICheckerManager>(); services.AddSingleton<ICheckerManager>(manager); }));
        Assert.Contains(runner.Id.ToString(), html.Body!.TextContent);
        Assert.Contains("private compiler version", html.Body.TextContent);
        Assert.Contains("compiler-details", html.Body.TextContent);
        Assert.Contains(SiteResources.StaffSelfTestFailed, html.Body.TextContent);
        Assert.Contains(SiteResources.StaffExpected, html.Body.TextContent);
        Assert.Contains(SiteResources.StaffActual, html.Body.TextContent);
        Assert.Equal("int main(void) { return 0; }", html.QuerySelector("#run-source")!.TextContent);
        Assert.Equal("custom input", html.QuerySelector("#run-input")!.TextContent);
        Assert.DoesNotContain(html.QuerySelectorAll("button"), button => button.TextContent == SiteResources.StaffAddCompiler);
        Assert.NotEmpty(runner.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JurySavedStandingsRetainHtmlAndOfficialSelection(bool official)
    {
        var author = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Saved jury", DateTimeOffset.Now.AddDays(-2), DateTimeOffset.Now.AddDays(-1));
        contest.IsHidden = true;
        contest.FinalTable = new Blob { Data = System.Text.Encoding.GetEncoding(1251).GetBytes("<table><tr><td>Full jury results</td></tr></table>") };
        contest.OfficialTable = new Blob { Data = System.Text.Encoding.GetEncoding(1251).GetBytes("<table><tr><td>Official jury results</td></tr></table>") };
        db.Contests.Add(contest);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryStanding>(
            $"/jury/contests/{contest.Id}/standing?Official={official}", new() { ["ContestId"] = contest.Id }, Staff(author.Id, RoleNames.Jury)));
        Assert.Equal(official ? "Official jury results" : "Full jury results", html.QuerySelector("[data-saved-standing] td")!.TextContent);
    }

}

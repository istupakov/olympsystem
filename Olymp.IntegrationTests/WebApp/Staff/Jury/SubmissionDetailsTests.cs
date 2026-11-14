using System.Reflection;
using System.Security.Claims;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Components.Submissions;
using Olymp.WebApp.Resources;

using Xunit;

using JuryDetails = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionDetails;
using JuryProblems = Olymp.WebApp.Components.Pages.Staff.Jury.Problems;
using JurySubmissions = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionList;
using Organizations = Olymp.WebApp.Components.Pages.Staff.Admin.OrganizationList;
using Users = Olymp.WebApp.Components.Pages.Staff.Admin.UserList;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

[Trait("Category", "Integration")]
public partial class SubmissionDetailsTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckHistoryShowsStoredScoresForEveryContestFormat(bool school)
    {
        var author = await _factory.AddUserAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Check history", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        contest.Abbr = school ? "school-history" : "icpc-history";
        var submission = TestEntities.CreateSubmission(author, TestEntities.CreateProblem(contest),
            TestEntities.CreateCompilator(), DateTimeOffset.UtcNow);
        submission.User = null!;
        submission.CheckResults.Add(new CheckResult
        {
            Submission = submission,
            CheckTime = DateTimeOffset.UtcNow,
            StatusCode = 2,
            Score = 37,
            TestResults = "SCORES_BY_TEST",
            Log = "Compilation:\nuser time: 1\nExecution log"
        });
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryDetails>(
            $"/jury/submissions/{submission.Id}", new() { [nameof(JuryDetails.Id)] = submission.Id }, Staff(author.Id, RoleNames.Jury)));
        var history = Assert.Single(page.QuerySelectorAll("details[data-check-id]"));
        Assert.Contains("SCORES_BY_TEST", history.TextContent);
        Assert.Equal("Compilation:\nuser time: 1\nExecution log", history.QuerySelector("pre")!.TextContent);
    }
    [Fact]
    public async Task AuthorLinkUsesExactAccountWithinContestAndBackLinkRequiresListContext()
    {
        var author = await _factory.AddUserAsync();
        var namesake = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Author", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(5));
        var other = TestEntities.CreateContest("Other", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(5));
        var problem = TestEntities.CreateProblem(contest);
        var compiler = TestEntities.CreateCompilator();
        var own = TestEntities.CreateSubmission(author, problem, compiler, DateTimeOffset.Now);
        var another = TestEntities.CreateSubmission(namesake, problem, compiler, DateTimeOffset.Now);
        var foreign = TestEntities.CreateSubmission(author, TestEntities.CreateProblem(other), compiler, DateTimeOffset.Now);
        own.User = another.User = foreign.User = null!;
        db.Submissions.AddRange(own, another, foreign);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var details = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryDetails>(
            $"/jury/submissions/{own.Id}", new() { [nameof(JuryDetails.Id)] = own.Id }));
        Assert.DoesNotContain(details.QuerySelectorAll("a"), link => link.TextContent == SiteResources.StaffBackToSubmissions);
        string authorUrl = Assert.Single(details.QuerySelectorAll("a[href*='AuthorId=']")).GetAttribute("href")!;
        Assert.EndsWith($"/submissions?AuthorId={author.Id}", authorUrl);
        Assert.Equal(own.Id, Assert.Single(await ReadStaffRowsAsync(authorUrl, contest.Id)).Id);
        string back = $"/jury/contests/{contest.Id}/submissions?Page=2";
        using var fromList = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryDetails>(
            $"/jury/submissions/{own.Id}?ReturnUrl={Uri.EscapeDataString(back)}", new() { [nameof(JuryDetails.Id)] = own.Id }));
        Assert.NotNull(fromList.QuerySelector($"a[href='{back}']"));
    }

    [Fact]
    public async Task SubmissionLinksOnlyExistingFailedTestsInCurrentResultAndHistory()
    {
        var author = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var problem = TestEntities.CreateProblem(TestEntities.CreateContest("Failed tests", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)));
        var tests = new[] {
            new Test { Problem = problem, Number = 7, Input = [], Output = [], IsActive = false },
            new Test { Problem = problem, Number = 3, Input = [], Output = [], IsActive = true }
        };
        var submission = TestEntities.CreateSubmission(author, problem, TestEntities.CreateCompilator(), DateTimeOffset.UtcNow);
        submission.User = null!;
        submission.StatusCode = -1007;
        foreach (int code in new[] { -2003, -1, -10 })
            submission.CheckResults.Add(new CheckResult { Submission = submission, CheckTime = DateTimeOffset.UtcNow, StatusCode = code, Log = "" });
        db.Submissions.Add(submission);
        db.Tests.AddRange(tests);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<SubmissionDetails>(
            $"/jury/submissions/{submission.Id}", new() { ["Id"] = submission.Id }, Staff(author.Id, RoleNames.Jury)));
        var links = page.QuerySelectorAll($"a[href^='jury/problems/{problem.Id}/tests/']");
        Assert.Equal(2, links.Length);
        Assert.Contains(links, item => item.GetAttribute("href") == $"jury/problems/{problem.Id}/tests/{tests[0].Id}");
        Assert.Contains(links, item => item.GetAttribute("href") == $"jury/problems/{problem.Id}/tests/{tests[1].Id}");
        submission.StatusCode = -1999;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var missing = await new HtmlParser().ParseDocumentAsync(await RenderAsync<SubmissionDetails>(
            $"/jury/submissions/{submission.Id}", new() { ["Id"] = submission.Id }, Staff(author.Id, RoleNames.Jury)));
        Assert.Single(missing.QuerySelectorAll($"a[href^='jury/problems/{problem.Id}/tests/']"));
    }
}

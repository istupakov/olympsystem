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
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Components.Submissions;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.AntiCheat;

using Xunit;

using JuryDetails = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionDetails;
using JuryProblems = Olymp.WebApp.Components.Pages.Staff.Jury.Problems;
using JurySubmissions = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionList;
using Organizations = Olymp.WebApp.Components.Pages.Staff.Admin.OrganizationList;
using Users = Olymp.WebApp.Components.Pages.Staff.Admin.UserList;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

[Trait("Category", "Integration")]
public partial class ProblemWorkspaceTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Fact]
    public async Task SubmittingAfterReturningToTaskClearsCodeAndRefreshesOwnList()
    {
        var author = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Stay on task", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(5));
        var problem = TestEntities.CreateProblem(contest);
        var compiler = TestEntities.CreateCompilator();
        db.Problems.Add(problem);
        db.Compilators.Add(compiler);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, author.Id.ToString()), new(ClaimTypes.Role, RoleNames.Jury)], "Test"));
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryProblems>(
            $"/jury/contests/{contest.Id}/problems?problemId={problem.Id}#submissions", new() { [nameof(JuryProblems.ContestId)] = contest.Id }, user,
            async component =>
            {
                var input = (SubmissionInput)typeof(JuryProblems).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;
                input.Code = "// Stay on task\nint main(void) { return 0; }";
                input.CompilatorId = compiler.Id;
                await (Task)typeof(JuryProblems).GetMethod("SubmitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null)!;
            }));
        Assert.Single(page.QuerySelectorAll("#submissions tbody tr"));
        Assert.Equal("", page.QuerySelector("#submission-code")!.TextContent);
        Assert.Equal(compiler.Id.ToString(), page.QuerySelector("#submission-compiler option[selected]")!.GetAttribute("value"));
        Assert.Equal(author.Id, await db.Submissions.Where(item => item.ProblemId == problem.Id).Select(item => item.UserId).SingleAsync(TestContext.Current.CancellationToken));
        using var adminPage = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryProblems>(
            $"/jury/contests/{contest.Id}/problems?problemId={problem.Id}", new() { [nameof(JuryProblems.ContestId)] = contest.Id }, Staff(author.Id)));
        Assert.Single(adminPage.QuerySelectorAll("#submissions tbody tr"));
        Assert.Empty(adminPage.QuerySelectorAll("#submissions a[href^='admin/']"));
        Assert.NotEmpty(adminPage.QuerySelectorAll("#submissions a[href^='jury/submissions/']"));
        var storedSubmission = await db.Submissions.Where(item => item.ProblemId == problem.Id).Select(item => item.Id).SingleAsync(TestContext.Current.CancellationToken);
        var source = adminPage.QuerySelectorAll("#submissions a").Single(link => link.TextContent == SiteResources.ViewSource);
        Assert.StartsWith($"jury/submissions/{storedSubmission}?returnUrl=", source.GetAttribute("href"));
        var download = adminPage.QuerySelectorAll("#submissions a").Single(link => link.TextContent == SiteResources.DownloadSource);
        Assert.Equal($"jury/submissions/{storedSubmission}/download", download.GetAttribute("href"));
        Assert.Equal("false", download.GetAttribute("data-enhance-nav"));
        var compilerCell = adminPage.QuerySelector("#submissions tbody tr")!.Children[2];
        Assert.Equal(compiler.Name, compilerCell.TextContent.Trim());
        Assert.Equal(compiler.Description, compilerCell.GetAttribute("title"));
    }

    [Fact]
    public async Task JuryProblemsShowInactiveUnpublishedStatementsAndOnlyCommonClarifications()
    {
        var author = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Unpublished", DateTimeOffset.Now.AddDays(1), DateTimeOffset.Now.AddDays(2));
        contest.IsHidden = true;
        var problem = TestEntities.CreateProblem(contest);
        problem.IsActive = false;
        problem.Text = "<strong>Unpublished statement</strong>";
        var second = TestEntities.CreateProblem(contest, 2);
        db.Problems.AddRange(problem, second);
        db.Messages.AddRange(
            new Message { Contest = contest, Problem = problem, JuryText = "Common clarification" },
            new Message { Contest = contest, Problem = problem, UserId = author.Id, JuryText = "Private reply" },
            new Message { Contest = contest, Problem = second, JuryText = "Other problem" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, author.Id.ToString()), new(ClaimTypes.Role, RoleNames.Jury)], "Test"));
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryProblems>(
            $"/jury/contests/{contest.Id}/problems?problemId={problem.Id}", new() { [nameof(JuryProblems.ContestId)] = contest.Id }, user));
        Assert.Equal("Unpublished statement", page.QuerySelector(".problem-statement strong")!.TextContent);
        Assert.Contains("Common clarification", page.QuerySelector(".problem-statement .jury-announcements")!.TextContent);
        Assert.DoesNotContain("Private reply", page.DocumentElement.OuterHtml);
        Assert.DoesNotContain("Other problem", page.QuerySelector(".problem-statement")!.TextContent);
        Assert.Equal(2, page.QuerySelectorAll(".problem-list .list-group-item").Length);
        Assert.NotNull(page.QuerySelector(".problem-list .badge"));
        Assert.NotNull(page.QuerySelector("#submission-code"));
        Assert.Empty(page.QuerySelectorAll("a[href*='downloadproblems']"));
    }

}

using System.Reflection;
using System.Security.Claims;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Checker;

using Xunit;

using JuryMessages = Olymp.WebApp.Components.Pages.Staff.Jury.Messages;
using JuryRunners = Olymp.WebApp.Components.Pages.Staff.Jury.Runners;
using JuryStanding = Olymp.WebApp.Components.Pages.Staff.Jury.Standing;
using JurySubmissions = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionList;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

[Trait("Category", "Integration")]
public partial class SubmissionRetestTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData(RoleNames.Admin, false, false)]
    [InlineData(RoleNames.Admin, true, true)]
    [InlineData(RoleNames.Jury, true, false)]
    public async Task RetestRequiresExplicitUrlModeAndAdministratorAccess(string role, bool enabled, bool visible)
    {
        var actor = await _factory.AddUserAsync();
        string path = "/jury/submissions" + (enabled ? "?Retest=true" : "");
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>(path, [], Staff(actor.Id, role),
            component => ActAsync(component, "PrepareRetestAsync")));
        Assert.Equal(visible, page.QuerySelectorAll("button").Any(button => button.TextContent == SiteResources.StaffRetestFiltered));
        Assert.Equal(visible, page.QuerySelectorAll("section").Any(section => section.GetAttribute("aria-label") == SiteResources.StaffRetestFiltered));
        if (visible)
        {
            string hide = page.QuerySelectorAll("a").Single(link => link.TextContent == SiteResources.StaffHideRetest).GetAttribute("href")!;
            Assert.DoesNotContain("Retest=", hide);
        }
    }

    [Fact]
    public async Task FilteredRetestCoversAllPagesWarnsAgainForNewAcceptedResultsAndRetainsHistory()
    {
        var author = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
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
        string url = $"/jury/submissions?Retest=true&Contest={contest.Id}&AuthorId={author.Id}&Compiler={compiler.Id}";
        using var preview = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>(url, [], Staff(author.Id),
            page => ActAsync(page, "PrepareRetestAsync")));
        Assert.Equal(25, (await ReadStaffRowsAsync(url)).Count);
        Assert.Null(preview.QuerySelector(".pagination"));
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
        var author = await _factory.AddUserAsync();
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>("/jury/submissions?Retest=true", [], Staff(author.Id, RoleNames.Jury),
            async component => { await ActAsync(component, "PrepareRetestAsync"); await ActAsync(component, "RetestAsync"); }));
        Assert.DoesNotContain(page.QuerySelectorAll("button"), button => button.TextContent == SiteResources.StaffRetestFiltered);
    }

}

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
public partial class StandingTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Fact]
    public async Task JuryStandingShowsHiddenFrozenContestWithoutChangingPublicAccess()
    {
        var author = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
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
        using var client = await _factory.LoginAsync(author.UserName!);
        using var publicPage = await client.GetAsync($"/contests/{contest.Id}/standing", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, publicPage.StatusCode);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JurySavedStandingsRetainHtmlAndOfficialSelection(bool official)
    {
        var author = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
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

using System.Net;
using System.Security.Claims;

using AngleSharp.Dom;
using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class ProblemAccessTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("guest", "/Account/Login")]
    [InlineData("user", "/Account/AccessDenied")]
    [InlineData("member", "/Account/AccessDenied")]
    [InlineData("coach", null)]
    [InlineData("pending-coach", null)]
    [InlineData("unrelated-coach", "/Account/AccessDenied")]
    [InlineData("other-contest-coach", "/Account/AccessDenied")]
    [InlineData("linked-no-claim", "/Account/AccessDenied")]
    [InlineData("legacy-coach", "/Account/Manage/Email")]
    [InlineData(RoleNames.Jury, "/Account/AccessDenied")]
    [InlineData(RoleNames.Admin, "/Account/AccessDenied")]
    public async Task EarlyPdfRequiresCoachClaimAndOwnParticipantInContestAndPreservesIdentityPolicies(string actor, string? deniedPath)
    {
        var data = await CreateDataAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Contests.Where(item => item.Id == data.Contest.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.StartTime, DateTimeOffset.UtcNow.AddHours(1))
                .SetProperty(item => item.EndTime, DateTimeOffset.UtcNow.AddHours(5))
                .SetProperty(item => item.ProblemShowTime, DateTimeOffset.UtcNow.AddMinutes(-1)), TestContext.Current.CancellationToken);
        }
        var user = actor == "legacy-coach" ? await factory.AddUserAsync(legacyLogin: true)
            : actor == "member" ? data.User : data.Other;
        if (actor is "coach" or "pending-coach" or "legacy-coach" or "linked-no-claim" or "other-contest-coach")
        {
            int teamId = actor == "other-contest-coach" ? (await CreateDataAsync()).Team.Id : data.Team.Id;
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var team = await db.Competitors.SingleAsync(item => item.Id == teamId, TestContext.Current.CancellationToken);
            team.CoachId = user.Id;
            team.IsApproved = actor != "pending-coach";
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        if (actor.Contains("coach", StringComparison.Ordinal))
            await AddClaimAsync(user, ClaimNames.CoachOrg, "99");
        if (actor is RoleNames.Jury or RoleNames.Admin)
            await AddRoleAsync(user, actor);
        using var client = actor == "guest" ? factory.CreateSiteClient() : await factory.LoginAsync(user.UserName!);
        string path = $"/contests/{data.Contest.Id}/printproblems";
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        if (deniedPath is null)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(deniedPath, new Uri(client.BaseAddress!, response.Headers.Location!).AbsolutePath);
            Assert.Equal(path, QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, response.Headers.Location!).Query)["returnUrl"]);
        }
        foreach (string query in new[] { "", "?forPrinting=true" })
        {
            using var publicDownload = await client.GetAsync($"/contests/{data.Contest.Id}/downloadproblems{query}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, publicDownload.StatusCode);
        }
        using var page = await GetAsync(client, $"/contests/{data.Contest.Id}/rules");
        var printLink = page.QuerySelector($"a[href='contests/{data.Contest.Id}/printproblems']");
        Assert.NotNull(printLink);
        Assert.Equal("Download statements for printing (PDF)", printLink.TextContent);
        Assert.Null(page.QuerySelector($"a[href='contests/{data.Contest.Id}/downloadproblems']"));
    }

    [Theory]
    [InlineData("before-print", false)]
    [InlineData("no-print-time", false)]
    [InlineData("hidden", false)]
    [InlineData("missing", false)]
    [InlineData("printing", true)]
    [InlineData("started", true)]
    public async Task PrintingRespectsPublicationAndRemainsAvailableAfterStart(string state, bool allowed)
    {
        var data = await CreateDataAsync();
        await AddClaimAsync(data.Other, ClaimNames.CoachOrg, "99");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var contest = await db.Contests.SingleAsync(item => item.Id == data.Contest.Id, TestContext.Current.CancellationToken);
            var team = await db.Competitors.SingleAsync(item => item.Id == data.Team.Id, TestContext.Current.CancellationToken);
            team.CoachId = state == "started" ? null : data.Other.Id;
            contest.StartTime = DateTimeOffset.UtcNow.AddHours(state == "started" ? -1 : 1);
            contest.EndTime = contest.StartTime.AddHours(5);
            contest.ProblemShowTime = state is "no-print-time" or "started" ? null
                : DateTimeOffset.UtcNow.AddMinutes(state == "before-print" ? 30 : -1);
            contest.IsHidden = state == "hidden";
            if (state == "missing")
                contest.ProblemPdfId = null;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = await factory.LoginAsync(data.Other.UserName!);
        using var response = await client.GetAsync($"/contests/{data.Contest.Id}/printproblems", TestContext.Current.CancellationToken);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
        if (allowed)
        {
            Assert.Equal("%PDF-test", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(data.Contest.Abbr + "problems.pdf", response.Content.Headers.ContentDisposition!.FileNameStar);
            using var page = await GetAsync(client, $"/contests/{data.Contest.Id}/rules");
            Assert.Equal(state == "started", page.QuerySelector($"a[href='contests/{data.Contest.Id}/downloadproblems']") is not null);
            Assert.Equal(state != "started", page.QuerySelector($"a[href='contests/{data.Contest.Id}/printproblems']") is not null);
        }
    }

    [Theory]
    [InlineData("member", true)]
    [InlineData("pending-member", true)]
    [InlineData("legacy-member", false)]
    [InlineData("coach", true)]
    [InlineData("linked-coach-without-claim", true)]
    [InlineData("user", false)]
    [InlineData("unrelated-coach", false)]
    [InlineData("other-contest-member", false)]
    [InlineData("other-contest-coach", false)]
    [InlineData("participant", false)]
    [InlineData(RoleNames.Jury, false)]
    [InlineData(RoleNames.Admin, false)]
    public async Task TestArchivesRequirePermanentMemberOrCoachRelationshipInThisContest(string actor, bool allowed)
    {
        var data = await CreateDataAsync();
        User user = data.Other;
        if (actor is "member" or "pending-member")
            user = data.User;
        else if (actor == "legacy-member")
            user = await factory.AddUserAsync(legacyLogin: true);
        else if (actor == "participant")
            user = await ParticipantAsync(data.Contest);
        else if (actor == "other-contest-member")
            user = (await CreateDataAsync()).User;
        if (actor is "coach" or "linked-coach-without-claim" or "pending-member" or "other-contest-coach" or "legacy-member")
        {
            int teamId = actor == "other-contest-coach" ? (await CreateDataAsync()).Team.Id : data.Team.Id;
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var team = await db.Competitors.Include(item => item.Members)
                .SingleAsync(item => item.Id == teamId, TestContext.Current.CancellationToken);
            if (actor == "pending-member")
                team.IsApproved = false;
            else if (actor == "legacy-member")
            {
                db.Attach(user);
                team.Members.Add((OlympUser)user);
            }
            else
                team.CoachId = user.Id;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        if (actor is "coach" or "unrelated-coach" or "other-contest-coach")
            await AddClaimAsync((OlympUser)user, ClaimNames.CoachOrg, "99");
        if (actor is RoleNames.Jury or RoleNames.Admin)
            await AddRoleAsync((OlympUser)user, actor);
        using var client = await factory.LoginAsync(user.UserName!);
        string path = $"/contests/{data.Contest.Id}/downloadtests";
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        if (allowed)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal(data.Contest.Abbr + "tests.zip", response.Content.Headers.ContentDisposition!.FileNameStar);
            Assert.Equal(new byte[] { 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        }
        else
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var location = new Uri(client.BaseAddress!, response.Headers.Location!);
            Assert.Equal(actor == "legacy-member" ? "/Account/Manage/Email" : "/Account/AccessDenied", location.AbsolutePath);
            Assert.Equal(path, QueryHelpers.ParseQuery(location.Query)["ReturnUrl"]);
        }
    }

    private async Task<(OlympUser User, OlympUser Other, Contest Contest, Problem Problem, Compilator Compiler, Competitor Team)>
        CreateDataAsync(bool open = true, bool running = false)
    {
        var user = await factory.AddUserAsync();
        var other = await factory.AddUserAsync();
        var now = DateTimeOffset.UtcNow;
        var contest = TestEntities.CreateContest($"Access {Guid.NewGuid():N}", now.AddHours(-2), running ? now.AddHours(2) : now.AddHours(-1));
        contest.IsOpen = open;
        contest.ProblemPdf = new Blob { Data = "%PDF-test"u8.ToArray() };
        contest.TestZip = new Blob { Data = [1, 2, 3] };
        var problem = TestEntities.CreateProblem(contest);
        problem.Text = "<p>Visible statement</p>";
        contest.Problems.Add(problem);
        var team = TestEntities.CreateCompetitor(contest, "Own team");
        team.Members.Add(user);
        contest.Competitors.Add(team);
        var compiler = TestEntities.CreateCompilator();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(user);
        db.AddRange(contest, compiler);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (user, other, contest, problem, compiler, team);
    }

    private async Task<Competitor> ParticipantAsync(Contest contest)
    {
        var participant = TestEntities.CreateCompetitor(contest, $"temp_{Guid.NewGuid():N}");
        participant.UserName = participant.Name;
        participant.Contest = null!;
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.True((await users.CreateAsync(participant, WebAppFactory.Password)).Succeeded);
        Assert.True((await users.AddClaimAsync(participant, new Claim(ClaimNames.CompetitorContest, contest.Id.ToString()))).Succeeded);
        return participant;
    }

    private async Task AddClaimAsync(OlympUser user, string type, string value)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.True((await users.AddClaimAsync((await users.FindByIdAsync(user.Id.ToString()))!, new Claim(type, value))).Succeeded);
    }

    private async Task AddRoleAsync(OlympUser user, string role)
    {
        using var scope = factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<int>>>();
        if (!await roles.RoleExistsAsync(role))
            Assert.True((await roles.CreateAsync(new IdentityRole<int>(role))).Succeeded);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(user.Id.ToString()))!, role)).Succeeded);
    }

    private static async Task<IDocument> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await ParseAsync(response);
    }

    private static async Task<IDocument> ParseAsync(HttpResponseMessage response) => await new HtmlParser().ParseDocumentAsync(
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

}

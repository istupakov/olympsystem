using System.Net;
using System.Security.Claims;

using AngleSharp.Dom;
using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Submissions;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class ProblemAccessTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    private const string Source = "// Submitted source\nclass Program { static void Main() {} }";

    [Theory]
    [InlineData(true, true, "user", true)]
    [InlineData(true, false, "user", true)]
    [InlineData(false, true, "user", false)]
    [InlineData(false, false, "user", false)]
    [InlineData(false, true, "ooc", false)]
    [InlineData(true, true, "ooc", true)]
    [InlineData(true, false, "ooc", true)]
    [InlineData(false, false, "ooc", false)]
    [InlineData(false, true, "pending-ooc", false)]
    [InlineData(false, true, "disqualified-team", false)]
    [InlineData(false, true, "official-member", false)]
    [InlineData(true, true, "disqualified-user", false)]
    [InlineData(false, true, "participant", true)]
    [InlineData(true, true, "participant", true)]
    [InlineData(true, false, "participant", false)]
    [InlineData(false, false, "participant", false)]
    [InlineData(false, true, "revoked-participant", false)]
    [InlineData(false, true, RoleNames.Jury, false)]
    [InlineData(false, true, RoleNames.Admin, false)]
    public async Task SubmissionPermissionAndAttributionMatchContestState(bool open, bool running, string actor, bool allowed)
    {
        var data = await CreateDataAsync(open, running);
        int expectedUserId = data.User.Id;
        HttpClient client;
        if (actor is "participant" or "revoked-participant")
        {
            var participant = await ParticipantAsync(data.Contest);
            expectedUserId = participant.Id;
            client = await factory.LoginAsync(participant.UserName!);
            if (actor == "revoked-participant")
            {
                using var scope = factory.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors
                    .Where(team => team.Id == participant.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(team => team.IsApproved, false), TestContext.Current.CancellationToken);
            }
        }
        else
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var team = await db.Competitors.SingleAsync(item => item.Id == data.Team.Id, TestContext.Current.CancellationToken);
            team.IsOutOfCompetition = actor is "ooc" or "pending-ooc" or "disqualified-team";
            team.IsApproved = actor != "pending-ooc";
            team.IsDisqualified = actor == "disqualified-team";
            if (actor == "disqualified-user")
                (await db.OlympUsers.SingleAsync(user => user.Id == data.User.Id, TestContext.Current.CancellationToken)).IsDisqualified = true;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            if (actor is RoleNames.Jury or RoleNames.Admin)
                await AddRoleAsync(data.User, actor);
            client = await factory.LoginAsync(data.User.UserName!);
        }
        using (client)
        {
            string path = Path(data.Contest, data.Problem);
            using var page = await GetAsync(client, path);
            Assert.Equal(allowed, page.QuerySelector("#submission-code") is not null);
            Assert.Equal(allowed, page.QuerySelector("a[href$='#submit-heading']") is not null);
            using var posted = await PostAsync(client, path, page, data.Compiler.Id);
            Assert.Equal(allowed ? HttpStatusCode.Redirect : HttpStatusCode.BadRequest, posted.StatusCode);
            using var check = factory.Services.CreateScope();
            var submissions = await check.ServiceProvider.GetRequiredService<OlympContext>().Submissions
                .Where(item => item.ProblemId == data.Problem.Id).ToListAsync(TestContext.Current.CancellationToken);
            if (allowed)
                Assert.Equal(expectedUserId, Assert.Single(submissions).UserId);
            else
                Assert.Empty(submissions);
        }
    }

    [Theory]
    [InlineData("closed", true)]
    [InlineData("disqualified", true)]
    [InlineData("hidden", false)]
    [InlineData("inactive", false)]
    [InlineData("future", false)]
    public async Task OwnerHistoryDependsOnProblemVisibilityNotSubmissionPermission(string state, bool visible)
    {
        var data = await CreateDataAsync();
        int id = await AddSubmissionAsync(data.User.Id, data.Problem, data.Compiler);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        if (state == "closed")
            await db.Contests.Where(item => item.Id == data.Contest.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsOpen, false), TestContext.Current.CancellationToken);
        else if (state == "disqualified")
            await db.OlympUsers.Where(item => item.Id == data.User.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsDisqualified, true), TestContext.Current.CancellationToken);
        else if (state == "hidden")
            await db.Contests.Where(item => item.Id == data.Contest.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsHidden, true), TestContext.Current.CancellationToken);
        else if (state == "inactive")
            await db.Problems.Where(item => item.Id == data.Problem.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false), TestContext.Current.CancellationToken);
        else
            await db.Contests.Where(item => item.Id == data.Contest.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.StartTime, DateTimeOffset.UtcNow.AddHours(1)), TestContext.Current.CancellationToken);
        using var client = await factory.LoginAsync(data.User.UserName!);
        foreach (string path in new[] { $"/submissions/{id}", $"/submissions/{id}/download" })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(visible ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
        }
        if (visible)
        {
            using var page = await GetAsync(client, Path(data.Contest, data.Problem));
            Assert.Null(page.QuerySelector("#submission-code"));
            Assert.NotNull(page.QuerySelector($"#submission-{id}"));
        }
    }

    [Theory]
    [InlineData(true, "own-coach", true)]
    [InlineData(false, "own-coach", false)]
    [InlineData(true, "other-coach", false)]
    [InlineData(true, "no-coach-claim", true)]
    [InlineData(false, "no-coach-claim", false)]
    [InlineData(true, "hidden-own-coach", false)]
    [InlineData(true, "inactive-own-coach", false)]
    [InlineData(true, RoleNames.Jury, false)]
    [InlineData(true, RoleNames.Admin, false)]
    public async Task CoachCanReadOwnTeamSubmissionInOpenContest(bool open, string actor, bool allowed)
    {
        var data = await CreateDataAsync(open, running: true);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Competitors.Where(team => team.Id == data.Team.Id).ExecuteUpdateAsync(
                setters => setters.SetProperty(team => team.CoachId,
                    actor == "other-coach" ? data.User.Id
                        : actor is RoleNames.Jury or RoleNames.Admin ? (int?)null : data.Other.Id), TestContext.Current.CancellationToken);
            // The shared-feed freeze does not restrict an authorized submission details view.
            await db.Contests.Where(contest => contest.Id == data.Contest.Id).ExecuteUpdateAsync(
                setters => setters.SetProperty(contest => contest.FreezeTime, DateTimeOffset.UtcNow.AddMinutes(-10)), TestContext.Current.CancellationToken);
            if (actor == "hidden-own-coach")
                await db.Contests.Where(contest => contest.Id == data.Contest.Id).ExecuteUpdateAsync(
                    setters => setters.SetProperty(contest => contest.IsHidden, true), TestContext.Current.CancellationToken);
            if (actor == "inactive-own-coach")
                await db.Problems.Where(problem => problem.Id == data.Problem.Id).ExecuteUpdateAsync(
                    setters => setters.SetProperty(problem => problem.IsActive, false), TestContext.Current.CancellationToken);
        }
        if (actor.EndsWith("coach", StringComparison.Ordinal))
            await AddClaimAsync(data.Other, ClaimNames.CoachOrg, "7");
        if (actor is RoleNames.Jury or RoleNames.Admin)
            await AddRoleAsync(data.Other, actor);
        int id = await AddSubmissionAsync(data.Team.Id, data.Problem, data.Compiler, diagnostics: true);
        using var client = await factory.LoginAsync(data.Other.UserName!);
        using var response = await client.GetAsync($"/submissions/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
        if (allowed)
        {
            using var page = await ParseAsync(response);
            Assert.Equal(Source, page.QuerySelector("pre code")!.TextContent);
            Assert.Contains("Compilation error", page.QuerySelector("main")!.TextContent);
            Assert.Equal("PRIVATE_DIAGNOSTICS", page.QuerySelector("#compilation-errors pre")!.TextContent);
        }
        using var download = await client.GetAsync($"/submissions/{id}/download", TestContext.Current.CancellationToken);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.NotFound, download.StatusCode);
        if (allowed)
            Assert.Equal(Source, await download.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        int personalId = await AddSubmissionAsync(data.User.Id, data.Problem, data.Compiler);
        using var personal = await client.GetAsync($"/submissions/{personalId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, personal.StatusCode);
    }

    [Theory]
    [InlineData(true, "member", true)]
    [InlineData(false, "member", false)]
    [InlineData(true, "participant", true)]
    [InlineData(false, "participant", true)]
    public async Task TeamSubmissionAuthorHasAccessButLinkedMemberRequiresOpenContest(bool open, string actor, bool allowed)
    {
        var data = await CreateDataAsync(open, running: true);
        var participant = await ParticipantAsync(data.Contest);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var team = await db.Competitors.Include(item => item.Members)
                .SingleAsync(item => item.Id == participant.Id, TestContext.Current.CancellationToken);
            team.Members.Add(await db.OlympUsers.SingleAsync(item => item.Id == data.User.Id, TestContext.Current.CancellationToken));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        int id = await AddSubmissionAsync(participant.Id, data.Problem, data.Compiler, diagnostics: true);
        using var client = await factory.LoginAsync(actor == "participant" ? participant.UserName! : data.User.UserName!);
        using var response = await client.GetAsync($"/submissions/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
        if (allowed)
        {
            using var page = await ParseAsync(response);
            Assert.Equal(Source, page.QuerySelector("pre code")!.TextContent);
            Assert.Contains("Compilation error", page.QuerySelector("main")!.TextContent);
            Assert.Equal("PRIVATE_DIAGNOSTICS", page.QuerySelector("#compilation-errors pre")!.TextContent);
        }
        using var download = await client.GetAsync($"/submissions/{id}/download", TestContext.Current.CancellationToken);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.NotFound, download.StatusCode);
        if (allowed)
            Assert.Equal(Source, await download.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

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

    [Fact]
    public async Task TemporaryAccountCanReadOtherContestsButOnlySubmitToOwnAndReadOwnSource()
    {
        var data = await CreateDataAsync(running: true);
        var other = await CreateDataAsync();
        var participant = await ParticipantAsync(data.Contest);
        int ownId = await AddSubmissionAsync(participant.Id, data.Problem, data.Compiler);
        int foreignId = await AddSubmissionAsync(other.User.Id, other.Problem, other.Compiler);
        using var client = await factory.LoginAsync(participant.UserName!);
        using var own = await GetAsync(client, Path(data.Contest, data.Problem));
        using var history = await GetAsync(client, "/submissions");
        Assert.Single(history.QuerySelectorAll("#submissions tbody tr"));
        Assert.Null(history.QuerySelector("#submissions .nav"));
        foreach (string scope in new[] { "all", "participant", "coached" })
        {
            using var deniedScope = await client.GetAsync($"/submissions?scope={scope}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, deniedScope.StatusCode);
        }
        Assert.NotNull(own.QuerySelector("#submission-code"));
        Assert.Null(own.QuerySelector("header a[href='account/manage']"));
        Assert.Equal(participant.Name, own.QuerySelector("header .navbar-text")!.TextContent);
        using var source = await GetAsync(client, $"/submissions/{ownId}");
        Assert.Equal(Source, source.QuerySelector("pre code")!.TextContent);
        foreach (string path in new[]
        {
            Path(other.Contest, other.Problem), $"/contests/{other.Contest.Id}/competitors", $"/contests/{other.Contest.Id}/rules",
            $"/contests/{other.Contest.Id}/standing", $"/contests/{other.Contest.Id}/downloadproblems"
        })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var foreign = await GetAsync(client, Path(other.Contest, other.Problem));
        Assert.NotNull(foreign.QuerySelector(".statement-html"));
        Assert.Null(foreign.QuerySelector("#submission-code"));
        Assert.Null(foreign.QuerySelector($"#submission-{foreignId}"));
        using var tests = await client.GetAsync($"/contests/{other.Contest.Id}/downloadtests", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, tests.StatusCode);
        Assert.Equal("/Account/AccessDenied", new Uri(client.BaseAddress!, tests.Headers.Location!).AbsolutePath);
        foreach (string path in new[] { $"/submissions/{foreignId}", $"/submissions/{foreignId}/download" })
        {
            using var denied = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        using var list = await GetAsync(client, "/contests");
        Assert.Equal(2, list.QuerySelectorAll("main article").Length);
        Assert.Null(list.QuerySelector("header a[href='contests']"));
        Assert.Null(list.QuerySelector("header a[href='account/manage']"));
        using var foreignPost = await PostAsync(client, Path(other.Contest, other.Problem), own, other.Compiler.Id);
        Assert.Equal(HttpStatusCode.BadRequest, foreignPost.StatusCode);
        using var rejected = await ParseAsync(foreignPost);
        Assert.Null(rejected.QuerySelector("#submission-code"));
        using var check = factory.Services.CreateScope();
        var viewer = await check.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<User>>().CreateAsync(participant);
        var permission = await check.ServiceProvider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(viewer, other.Problem,
            new SubmitSolutionRequirement());
        Assert.False(permission.Succeeded);
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<OlympContext>().Submissions
            .CountAsync(item => item.ProblemId == other.Problem.Id, TestContext.Current.CancellationToken));
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

    private async Task<int> AddSubmissionAsync(int userId, Problem problem, Compilator compiler, bool diagnostics = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var submission = new Submission
        {
            User = null!,
            UserId = userId,
            Problem = null!,
            ProblemId = problem.Id,
            Compilator = null!,
            CompilatorId = compiler.Id,
            SourceCode = null!,
            Text = Source,
            CommitTime = DateTimeOffset.UtcNow,
            StatusCode = diagnostics ? -1 : 2,
            CheckResults = []
        };
        if (diagnostics)
            submission.CheckResults.Add(new CheckResult { Submission = submission, StatusCode = -1, CheckTime = DateTimeOffset.UtcNow, Log = "PRIVATE_DIAGNOSTICS" });
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return submission.Id;
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

    private static string Path(Contest contest, Problem problem) => $"/contests/{contest.Id}/problems?problemId={problem.Id}";

    private static async Task<IDocument> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await ParseAsync(response);
    }

    private static async Task<IDocument> ParseAsync(HttpResponseMessage response) => await new HtmlParser().ParseDocumentAsync(
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, IDocument page, int compilerId)
    {
        var form = page.QuerySelector("form:has(#submission-code)");
        var fields = form?.QuerySelectorAll("input[type='hidden'][name]")
            .ToDictionary(input => input.GetAttribute("name")!, input => input.GetAttribute("value") ?? "") ?? [];
        fields["_handler"] = "submit-solution";
        fields["Input.CompilatorId"] = compilerId.ToString();
        fields["Input.Code"] = Source;
        using var content = new FormUrlEncodedContent(fields);
        return await client.PostAsync(path, content, TestContext.Current.CancellationToken);
    }
}

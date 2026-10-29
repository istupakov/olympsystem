using System.Net;
using System.Security.Claims;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.Site.Contests;

[Collection("Contest management")]
public class AuthorizationTests(SiteFixture site) : IClassFixture<SiteFixture>
{
    [Fact]
    public async Task AmbiguousEmailLookupRequiresUsernameOrId()
    {
        var first = await site.AddUserAsync();
        var second = await site.AddUserAsync();
        using (var scope = site.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.True((await users.SetEmailAsync(second, first.Email)).Succeeded);
        }
        var admin = await site.AddUserAsync(RoleNames.Admin);
        using var client = await site.LoginAsync(admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/Users/Manage?email=" + Uri.EscapeDataString(first.Email!), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Users/Manage?username=" + Uri.EscapeDataString(first.UserName!), TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("Ban", RoleNames.Jury, true)]
    [InlineData("Ban", RoleNames.Jury, false)]
    [InlineData("RemoveRole", RoleNames.Jury, true)]
    [InlineData("RemoveRole", RoleNames.Admin, true)]
    [InlineData("RemoveCoachOrgClaim", null, true)]
    public async Task RestrictingAccessRevokesExistingCookieWithinTwoMinutes(string action, string? role, bool lockoutEnabled)
    {
        var user = await site.AddUserAsync(role);
        var protectedPage = role == RoleNames.Admin ? "/Users" : "/Checkers";
        if (action == "RemoveCoachOrgClaim")
        {
            await AddOrganizationAsync(user, coach: true);
            var contest = await AddRegistrationContestAsync(hidden: false, acm: true);
            protectedPage = $"/Contests/Registration/Teams/{contest.Id}";
        }
        using (var scope = site.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.True((await users.SetLockoutEnabledAsync((await users.FindByIdAsync(user.Id.ToString()))!, lockoutEnabled)).Succeeded);
        }
        var clock = new TestTimeProvider();
        using var factory = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<SecurityStampValidatorOptions>(options => options.TimeProvider = clock)));
        Assert.Equal(TimeSpan.FromMinutes(2), factory.Services.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value.ValidationInterval);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, "/Identity/Account/Login", new()
        {
            ["Input.Username"] = user.UserName!,
            ["Input.Password"] = SiteFixture.Password
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(protectedPage, TestContext.Current.CancellationToken)).StatusCode);
        var token = await SiteFixture.TokenAsync(client, "/Identity/Account/Manage");
        var admin = await site.AddUserAsync(RoleNames.Admin);
        using var adminClient = await site.LoginAsync(admin);
        var values = new Dictionary<string, string> { ["action"] = action };
        if (role is not null)
            values["role"] = role;
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(adminClient, $"/Users/Manage/{user.Id}", values)).StatusCode);

        // The configured interval permits the current cookie until its next validation.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(protectedPage, TestContext.Current.CancellationToken)).StatusCode);
        clock.Offset = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(5);
        var response = await client.PostAsync("/Identity/Account/Manage", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/identity/account/login", response.Headers.Location!.ToString().ToLowerInvariant());
        response = await client.GetAsync(protectedPage, TestContext.Current.CancellationToken);
        Assert.Contains("/identity/account/login", response.Headers.Location!.ToString().ToLowerInvariant());

        using var freshClient = site.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        response = await SiteFixture.PostAsync(freshClient, "/Identity/Account/Login", new()
        {
            ["Input.Username"] = user.UserName!,
            ["Input.Password"] = SiteFixture.Password
        });
        if (action == "Ban")
            Assert.Contains("lockout", response.Headers.Location!.ToString().ToLowerInvariant());
        else
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("AccessDenied", (await freshClient.GetAsync(protectedPage, TestContext.Current.CancellationToken)).Headers.Location!.ToString());
        }
    }

    [Theory]
    [InlineData("Disapprove")]
    [InlineData("Disqualify")]
    public async Task RestrictedCompetitorCannotSubmitOrSendMessagesWithExistingCookie(string action)
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        Competitor competitor;
        int problemId, compilerId;
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Contests.Where(x => x.Id == contest.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsHidden, false), cancellationToken: TestContext.Current.CancellationToken);
            await ContestManagementTests.ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.ProblemZip());
            problemId = await db.Problems.Where(x => x.ContestId == contest.Id).Select(x => x.Id).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            var compiler = new Compilator
            {
                Name = "auth-test",
                Description = "Auth test",
                Language = "C",
                ConfigName = "test",
                SourceExtension = ".c",
                CommandLine = "compile",
                IsActive = true,
                Submissions = []
            };
            db.Compilators.Add(compiler);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            compilerId = compiler.Id;
            competitor = new Competitor
            {
                Name = "Participant",
                UserName = "auth" + Guid.NewGuid().ToString("N"),
                PasswordHash = SiteFixture.Password,
                ContestId = contest.Id,
                Contest = null!,
                IsApproved = true,
                SecurityStamp = Guid.NewGuid().ToString(),
                Members = [],
                Messages = [],
                Submissions = []
            };
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.True((await users.CreateAsync(competitor)).Succeeded);
            Assert.True((await users.AddClaimAsync(competitor, new(ClaimNames.CompetitorContest, contest.Id.ToString()))).Succeeded);
        }
        using var client = await site.LoginAsync(competitor);
        var submissionsPath = $"/Contests/Submits/{contest.Id}";
        var messagesPath = $"/Contests/Submits/SendMessage/{contest.Id}";
        var submit = new Dictionary<string, string>
        {
            ["Input.ProblemId"] = problemId.ToString(),
            ["Input.CompilatorId"] = compilerId.ToString(),
            ["Input.Code"] = "int main() { return 0; }"
        };
        var message = new Dictionary<string, string> { ["Input.Text"] = "Question for the jury" };
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, submissionsPath, submit)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, messagesPath, message)).StatusCode);
        using var ownerClient = await site.LoginAsync(owner);
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(ownerClient, $"/Contests/Competitors/Manage/{contest.Id}", new()
        {
            ["userId"] = competitor.Id.ToString(),
            ["action"] = action
        })).StatusCode);
        foreach (var path in new[] { submissionsPath, messagesPath })
            Assert.Contains("AccessDenied", (await client.GetAsync(path, TestContext.Current.CancellationToken)).Headers.Location!.ToString());
        foreach (var (path, values) in new[] { (submissionsPath, submit), (messagesPath, message) })
            Assert.Contains("AccessDenied", (await client.PostAsync(path, new FormUrlEncodedContent(values), TestContext.Current.CancellationToken)).Headers.Location!.ToString());
        using var finalScope = site.Services.CreateScope();
        var saved = finalScope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.Equal(1, await saved.Submissions.CountAsync(x => x.UserId == competitor.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await saved.Messages.CountAsync(x => x.UserId == competitor.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IndividualRegistrationRequiresVisibleIndividualContest(bool hidden, bool acm)
    {
        var user = await site.AddUserAsync();
        await AddOrganizationAsync(user);
        var contest = await AddRegistrationContestAsync(hidden, acm);
        using var client = await site.LoginAsync(user);
        var path = $"/Contests/Registration/{contest.Id}";
        Assert.Equal(hidden ? HttpStatusCode.NotFound : HttpStatusCode.OK, (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
        var response = await SiteFixture.PostAsync(client, path + "?handler=Create", new() { ["ooc"] = "true" }, "/Identity/Account/Manage");
        if (hidden)
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        else if (acm)
            Assert.Contains("AccessDenied", response.Headers.Location!.ToString());
        else
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.Equal(!hidden && !acm, await db.Competitors.AnyAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken));
        if (!hidden && !acm)
        {
            Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path + "?handler=Delete", [])).StatusCode);
            Assert.False(await db.Competitors.AnyAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public async Task TeamRegistrationRequiresVisibleAcmContestAndCoachedOrganization(bool hidden, bool acm, bool ownOrganization)
    {
        var coach = await site.AddUserAsync();
        var orgId = await AddOrganizationAsync(coach, coach: true);
        if (!ownOrganization)
            orgId = await AddOrganizationAsync(await site.AddUserAsync());
        var contest = await AddRegistrationContestAsync(hidden, acm);
        using var client = await site.LoginAsync(coach);
        var path = $"/Contests/Registration/Teams/Create/{contest.Id}?orgId={orgId}";
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        if (hidden || !acm)
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        else if (!ownOrganization)
            Assert.Contains("AccessDenied", response.Headers.Location!.ToString());
        else
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(hidden || !acm ? HttpStatusCode.NotFound : HttpStatusCode.OK,
            (await client.GetAsync($"/Contests/Registration/Teams/{contest.Id}", TestContext.Current.CancellationToken)).StatusCode);
        response = await SiteFixture.PostAsync(client, path, new()
        {
            ["Input.TeamPrefix"] = "NSTU",
            ["Input.Participant1"] = "Первый",
            ["Input.Participant2"] = "Второй",
            ["Input.Participant3"] = "Третий",
            ["Input.Confirmation"] = "true"
        }, "/Identity/Account/Manage");
        if (hidden || !acm)
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        else if (!ownOrganization)
            Assert.Contains("AccessDenied", response.Headers.Location!.ToString());
        else
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.Equal(!hidden && acm && ownOrganization, await db.Competitors.AnyAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken));
        if (!hidden && acm && ownOrganization)
        {
            var teamId = await db.Competitors.Where(x => x.ContestId == contest.Id).Select(x => x.Id).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, $"/Contests/Registration/Teams/{contest.Id}?handler=Delete", new()
            {
                ["teamId"] = teamId.ToString()
            })).StatusCode);
            Assert.False(await db.Competitors.AnyAsync(x => x.Id == teamId, cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RegistrationCannotBeDeletedThroughHiddenContestOrWrongRegistrationType(bool team, bool wrongType)
    {
        var user = await site.AddUserAsync();
        var orgId = await AddOrganizationAsync(user, coach: team);
        var contest = await AddRegistrationContestAsync(hidden: !wrongType, acm: team && !wrongType);
        int competitorId;
        using (var scope = site.Services.CreateScope())
        {
            var competitor = new Competitor
            {
                Name = "Registered participant",
                ContestId = contest.Id,
                Contest = null!,
                OrganizationId = orgId,
                CoachId = team ? user.Id : null,
                Members = [],
                Messages = [],
                Submissions = []
            };
            if (!team)
            {
                scope.ServiceProvider.GetRequiredService<OlympContext>().Attach(user);
                competitor.Members.Add(user);
            }
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(competitor)).Succeeded);
            competitorId = competitor.Id;
        }
        using var client = await site.LoginAsync(user);
        var path = team ? $"/Contests/Registration/Teams/{contest.Id}" : $"/Contests/Registration/{contest.Id}";
        var response = await SiteFixture.PostAsync(client, path + "?handler=Delete",
            new() { ["teamId"] = competitorId.ToString() }, "/Identity/Account/Manage");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var final = site.Services.CreateScope();
        Assert.True(await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors.AnyAsync(x => x.Id == competitorId, cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<int> AddOrganizationAsync(OlympUser user, bool coach = false)
    {
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var organization = new Organization { Name = "Test university", ACMName = "NSTU", Users = [] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync();
        await db.OlympUsers.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.OrganizationId, organization.Id).SetProperty(x => x.FirstName, "Иван")
            .SetProperty(x => x.LastName, "Иванов").SetProperty(x => x.Details, "A1"));
        if (coach)
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.True((await users.AddClaimAsync((await users.FindByIdAsync(user.Id.ToString()))!,
                new Claim(ClaimNames.CoachOrg, organization.Id.ToString()))).Succeeded);
        }
        return organization.Id;
    }

    private async Task<Contest> AddRegistrationContestAsync(bool hidden, bool acm)
    {
        var contest = await site.AddContestAsync(prefix: acm ? "acm" : "school");
        using var scope = site.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OlympContext>().Contests.Where(x => x.Id == contest.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsHidden, hidden).SetProperty(x => x.StartTime, DateTimeOffset.Now.AddHours(1)));
        return contest;
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        public TimeSpan Offset { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
    }
}

using System.Net;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Site.Services;
using Olymp.Site.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Site.Contests;

[Collection("Contest management")]
public class CompetitorRegistrationTests(SiteFixture site) : IClassFixture<SiteFixture>
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task RegistrationCopiesProfileAndCancellationPreservesUserAndOtherRegistration(
        bool outOfCompetition, bool hasGroup, bool hidden)
    {
        var token = TestContext.Current.CancellationToken;
        var (user, contest, orgId) = await SetupAsync();
        var other = await site.AddContestAsync();
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            await db.OlympUsers.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Details, hasGroup ? "A1" : null).SetProperty(x => x.IsHidden, hidden), token);
            await db.Contests.Where(x => x.Id == other.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsHidden, false), token);
            var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
            await service.RegisterIndividualAsync(contest.Id, user.Id, outOfCompetition, token);
            await service.RegisterIndividualAsync(other.Id, user.Id, outOfCompetition, token);
            await Assert.ThrowsAsync<OperationValidationException>(() => service.RegisterIndividualAsync(contest.Id, user.Id, outOfCompetition, token));
        }

        using (var verification = site.Services.CreateScope())
        {
            var db = verification.ServiceProvider.GetRequiredService<OlympContext>();
            var saved = await db.Competitors.Include(x => x.Members).SingleAsync(x => x.ContestId == contest.Id, token);
            Assert.Equal("Иван Иванов, Test university" + (hasGroup ? ", A1" : ""), saved.Name);
            Assert.Equal(orgId, saved.OrganizationId);
            Assert.Equal(outOfCompetition, saved.IsOutOfCompetition);
            Assert.Equal(hidden, saved.IsHidden);
            Assert.False(saved.IsApproved);
            Assert.NotNull(saved.RegistrationDate);
            Assert.NotNull(saved.SecurityStamp);
            Assert.Equal(user.Id, Assert.Single(saved.Members).Id);
        }

        using var client = await site.LoginAsync(user);
        var path = $"/Contests/Registration/{contest.Id}";
        using var response = await SiteFixture.PostAsync(client, path + "?handler=Delete", []);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var final = site.Services.CreateScope();
        var context = final.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False(await context.Competitors.AnyAsync(x => x.ContestId == contest.Id, token));
        Assert.True(await context.Competitors.AnyAsync(x => x.ContestId == other.Id && x.Members.Any(m => m.Id == user.Id), token));
        Assert.True(await context.OlympUsers.AnyAsync(x => x.Id == user.Id, token));
        Assert.True(await context.Organizations.AnyAsync(x => x.Id == orgId, token));
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("team")]
    [InlineData("ended")]
    [InlineData("firstName")]
    [InlineData("lastName")]
    [InlineData("organization")]
    [InlineData("group")]
    public async Task RegistrationRejectsUnavailableContestOrIncompleteProfile(string invalid)
    {
        var token = TestContext.Current.CancellationToken;
        var (user, contest, _) = await SetupAsync();
        using (var setup = site.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Contests.Where(x => x.Id == contest.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsHidden, invalid == "hidden")
                .SetProperty(x => x.Abbr, invalid == "team" ? "acm" + contest.Abbr : contest.Abbr)
                .SetProperty(x => x.EndTime, invalid == "ended" ? DateTimeOffset.Now.AddHours(-1) : contest.EndTime), token);
            await db.OlympUsers.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.FirstName, invalid == "firstName" ? null : "Иван")
                .SetProperty(x => x.LastName, invalid == "lastName" ? null : "Иванов")
                .SetProperty(x => x.OrganizationId, x => invalid == "organization" ? null : x.OrganizationId)
                .SetProperty(x => x.Details, invalid == "group" ? null : "A1"), token);
        }

        using (var scope = site.Services.CreateScope())
            await Assert.ThrowsAsync<OperationValidationException>(() => scope.ServiceProvider
                .GetRequiredService<CompetitorManagementService>().RegisterIndividualAsync(contest.Id, user.Id, false, token));
        using var final = site.Services.CreateScope();
        Assert.False(await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors.AnyAsync(x => x.ContestId == contest.Id, token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterSavingRegistrationRollsBackParticipantAndMembership(bool team)
    {
        var token = TestContext.Current.CancellationToken;
        var (user, contest, orgId) = await SetupAsync(team);
        Task RegisterAsync(CompetitorManagementService service, CancellationToken cancellationToken) => team
            ? service.RegisterTeamAsync(contest.Id, orgId, user.Id, "NSTU", [user.Id.ToString(), "Второй", "Третий"], cancellationToken)
            : service.RegisterIndividualAsync(contest.Id, user.Id, false, cancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var interceptor = new CancelAfterRegistrationSaved(cancellation);
        using var factory = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<OlympContext>(options => options.AddInterceptors(interceptor))));
        using (var scope = factory.Services.CreateScope())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RegisterAsync(scope.ServiceProvider.GetRequiredService<CompetitorManagementService>(), cancellation.Token));

        Assert.True(interceptor.RegistrationSaved);
        using (var verification = site.Services.CreateScope())
        {
            var db = verification.ServiceProvider.GetRequiredService<OlympContext>();
            Assert.False(await db.Competitors.AnyAsync(x => x.ContestId == contest.Id, token));
            Assert.False(await db.OlympUsers.Where(x => x.Id == user.Id).SelectMany(x => x.Memberships)
                .AnyAsync(x => x.ContestId == contest.Id, token));
        }

        using var retry = site.Services.CreateScope();
        await RegisterAsync(retry.ServiceProvider.GetRequiredService<CompetitorManagementService>(), token);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task TeamRegistrationPreservesMemberOrderAndCoachCanCancel(int linkedMembers)
    {
        var token = TestContext.Current.CancellationToken;
        var (user, contest, orgId) = await SetupAsync(team: true);
        var coach = await site.AddUserAsync();
        var slots = new[] { "Первый", "Второй", "Третий" };
        var names = new[] { "Первый", "Второй", "Третий" };
        var memberIds = new List<int>();
        using (var setup = site.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<OlympContext>();
            for (int i = 0; i < linkedMembers; i++)
            {
                var member = i == 0 ? user : await site.AddUserAsync();
                await db.OlympUsers.Where(x => x.Id == member.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.OrganizationId, orgId).SetProperty(x => x.FirstName, "Иван")
                    .SetProperty(x => x.LastName, "Иванов"), token);
                slots[i] = member.Id.ToString();
                names[i] = "Иванов Иван";
                memberIds.Add(member.Id);
            }
            Assert.True((await setup.ServiceProvider.GetRequiredService<UserManager<User>>()
                .AddClaimAsync(coach, new(ClaimNames.CoachOrg, orgId.ToString()))).Succeeded);
        }

        using (var scope = site.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
            await service.RegisterTeamAsync(contest.Id, orgId, coach.Id, "NSTU 1: Team", slots, token);
            if (linkedMembers > 0)
                await Assert.ThrowsAsync<OperationValidationException>(() =>
                    service.RegisterTeamAsync(contest.Id, orgId, coach.Id, "NSTU 2: Team", slots, token));
        }

        int teamId;
        using (var verification = site.Services.CreateScope())
        {
            var db = verification.ServiceProvider.GetRequiredService<OlympContext>();
            var team = await db.Competitors.Include(x => x.Members).SingleAsync(x => x.ContestId == contest.Id, token);
            teamId = team.Id;
            Assert.Equal("NSTU 1: Team", team.Name);
            Assert.Equal(coach.Id, team.CoachId);
            Assert.Equal(orgId, team.OrganizationId);
            Assert.False(team.IsApproved);
            Assert.NotNull(team.SecurityStamp);
            Assert.Equal(names, team.GetMembers()!.Select(x => x.name));
            Assert.Equal(memberIds, team.GetMembers()!.Where(x => x.user is not null).Select(x => x.user!.Id));
        }

        using var client = await site.LoginAsync(coach);
        using var response = await SiteFixture.PostAsync(client, $"/Contests/Registration/Teams/{contest.Id}?handler=Delete",
            new() { ["teamId"] = teamId.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var final = site.Services.CreateScope();
        var context = final.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False(await context.Competitors.AnyAsync(x => x.Id == teamId, token));
        Assert.Equal(memberIds.Count, await context.OlympUsers.CountAsync(x => memberIds.Contains(x.Id), token));
        Assert.True(await context.OlympUsers.AnyAsync(x => x.Id == coach.Id, token));
    }

    [Theory]
    [InlineData("hiddenMember")]
    [InlineData("foreignMember")]
    [InlineData("duplicateMember")]
    [InlineData("duplicateName")]
    [InlineData("missingMember")]
    [InlineData("separatorInName")]
    [InlineData("hiddenContest")]
    [InlineData("individualContest")]
    [InlineData("endedContest")]
    public async Task TeamRegistrationRejectsInvalidCompositionOrUnavailableContest(string invalid)
    {
        var token = TestContext.Current.CancellationToken;
        var (user, contest, orgId) = await SetupAsync(team: true);
        using (var setup = site.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<OlympContext>();
            await db.OlympUsers.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsHidden, invalid == "hiddenMember")
                .SetProperty(x => x.OrganizationId, x => invalid == "foreignMember" ? null : x.OrganizationId), token);
            await db.Contests.Where(x => x.Id == contest.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsHidden, invalid == "hiddenContest")
                .SetProperty(x => x.Abbr, invalid == "individualContest" ? "school-test" : contest.Abbr)
                .SetProperty(x => x.EndTime, invalid == "endedContest" ? DateTimeOffset.Now.AddHours(-1) : contest.EndTime), token);
        }

        string[] slots = invalid switch
        {
            "duplicateMember" => [user.Id.ToString(), "000" + user.Id, "Третий"],
            "duplicateName" => ["Первый", "Первый", "Третий"],
            "missingMember" => [user.Id.ToString(), "Второй"],
            "separatorInName" => [user.Id.ToString(), "Второй;Третий", "Четвёртый"],
            _ => [user.Id.ToString(), "Второй", "Третий"]
        };
        using (var scope = site.Services.CreateScope())
            await Assert.ThrowsAsync<OperationValidationException>(() => scope.ServiceProvider.GetRequiredService<CompetitorManagementService>()
                .RegisterTeamAsync(contest.Id, orgId, user.Id, "NSTU", slots, token));
        using var final = site.Services.CreateScope();
        Assert.False(await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors.AnyAsync(x => x.ContestId == contest.Id, token));
    }

    [Fact]
    public async Task TeamCreationShowsPreviewAndRequiresConfirmation()
    {
        var token = TestContext.Current.CancellationToken;
        var (user, contest, orgId) = await SetupAsync(team: true);
        var coach = await site.AddUserAsync();
        using (var setup = site.Services.CreateScope())
            Assert.True((await setup.ServiceProvider.GetRequiredService<UserManager<User>>()
                .AddClaimAsync(coach, new(ClaimNames.CoachOrg, orgId.ToString()))).Succeeded);
        using var client = await site.LoginAsync(coach);
        var path = $"/Contests/Registration/Teams/Create/{contest.Id}?orgId={orgId}";
        var values = new Dictionary<string, string>
        {
            ["Input.TeamPrefix"] = "NSTU",
            ["Input.TeamNumber"] = "1",
            ["Input.TeamSuffix"] = "Team",
            ["Input.Participant1"] = user.Id.ToString(),
            ["Input.Participant2"] = "Второй",
            ["Input.Participant3"] = "Третий"
        };
        using var preview = await SiteFixture.PostAsync(client, path, values, path);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var html = WebUtility.HtmlDecode(await preview.Content.ReadAsStringAsync(token));
        Assert.Contains("NSTU 1: Team", html);
        Assert.Contains("Иванов Иван, Второй, Третий", html);
        using (var verification = site.Services.CreateScope())
            Assert.False(await verification.ServiceProvider.GetRequiredService<OlympContext>().Competitors.AnyAsync(x => x.ContestId == contest.Id, token));

        values["Input.Confirmation"] = "true";
        using var response = await SiteFixture.PostAsync(client, path, values, path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var final = site.Services.CreateScope();
        Assert.Equal("NSTU 1: Team", (await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .SingleAsync(x => x.ContestId == contest.Id, token)).Name);
    }

    private async Task<(OlympUser User, Contest Contest, int OrgId)> SetupAsync(bool team = false)
    {
        var token = TestContext.Current.CancellationToken;
        var user = await site.AddUserAsync();
        var contest = await site.AddContestAsync(prefix: team ? "acm" : "school");
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var organization = new Organization { Name = "Test university", Users = [] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(token);
        await db.OlympUsers.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.FirstName, "Иван").SetProperty(x => x.LastName, "Иванов")
            .SetProperty(x => x.OrganizationId, organization.Id).SetProperty(x => x.Details, "A1"), token);
        await db.Contests.Where(x => x.Id == contest.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.IsHidden, false).SetProperty(x => x.StartTime, DateTimeOffset.Now.AddHours(1)), token);
        return (user, contest, organization.Id);
    }

    private sealed class CancelAfterRegistrationSaved(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        private bool _savingRegistration;
        public bool RegistrationSaved { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            _savingRegistration = eventData.Context!.ChangeTracker.Entries<Competitor>().Any(x => x.State == EntityState.Added);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (_savingRegistration)
            {
                RegistrationSaved = true;
                cancellation.Cancel();
            }
            return ValueTask.FromResult(result);
        }
    }
}

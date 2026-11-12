using System.Security.Claims;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Icpc;
using Olymp.WebApp;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

[Collection("Contest management")]
public class IcpcImportTests(ContestServiceFixture site) : IClassFixture<ContestServiceFixture>
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DefaultsToCoachOrganizationBeforeMatchingIcpcName(bool matchesIcpcName)
    {
        var token = TestContext.Current.CancellationToken;
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var coachOrganization = new Organization { Name = "Coach organization", Users = [] };
        db.Organizations.Add(coachOrganization);
        await db.SaveChangesAsync(token);
        await db.OlympUsers.Where(user => user.Id == setup.Coach.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(user => user.OrganizationId, coachOrganization.Id), token);
        var source = matchesIcpcName ? setup.Source : setup.Source with { InstName = "Unknown ICPC institution" };
        var service = scope.ServiceProvider.GetRequiredService<IcpcImportService>();

        var row = Assert.Single((await service.PreviewAsync(setup.Actor, setup.Contest.Id, [source], token)).Rows);

        Assert.Equal(coachOrganization.Id, row.OrganizationId);
        Assert.DoesNotContain(Olymp.WebApp.Resources.SiteResources.IcpcSelectOrganization, row.Warnings);
        Assert.True(row.CoachNeedsAccess);
    }

    [Fact]
    public async Task FailureWhileSavingSecondTeamRollsBackFirstTeamAndItsClaim()
    {
        var token = TestContext.Current.CancellationToken;
        var setup = await SetupAsync();
        var source = setup.Source with
        {
            Members = [new() { TeamId = 12345, Role = "CONTESTANT", FirstName = "Unlinked" }]
        };
        var other = source with { Id = 54321, Name = "Another imported team" };
        var interceptor = new FailSecondTeam();
        using var factory = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContextFactory<OlympContext>(options => options.AddInterceptors(interceptor))));
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IcpcImportService>();
            var preview = await service.PreviewAsync(setup.Actor, setup.Contest.Id, [source, other], token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(setup.Actor, setup.Contest.Id, preview.Rows, token));
        }
        using var verification = site.Services.CreateScope();
        var db = verification.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.NotEqual(0, interceptor.FirstId);
        Assert.False(await db.Competitors.AnyAsync(item => item.ContestId == setup.Contest.Id, token));
        Assert.False(await db.UserClaims.AnyAsync(claim => claim.UserId == interceptor.FirstId, token));
    }

    [Fact]
    public async Task ImportsExistingAccountsAndUpdatesByClaimWithoutChangingCredentialsOrFlags()
    {
        var token = TestContext.Current.CancellationToken;
        var setup = await SetupAsync();
        using (var scope = site.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IcpcImportService>();
            var preview = await service.PreviewAsync(setup.Actor, setup.Contest.Id, [setup.Source], token);
            var row = Assert.Single(preview.Rows);
            Assert.Equal(setup.Member.Id, Assert.Single(row.Members).UserId);
            Assert.Equal(setup.Coach.Id, row.CoachId);
            Assert.Equal(setup.OrgId, row.OrganizationId);
            Assert.True(row.CoachNeedsAccess);
            Assert.Empty(row.Errors);
            Assert.Equal(new IcpcImportSummary(1, 0), await service.ImportAsync(setup.Actor, setup.Contest.Id, preview.Rows, token));
        }
        int id;
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var team = Assert.Single(await db.Competitors.Where(item => item.ContestId == setup.Contest.Id).ToListAsync(token));
            id = team.Id;
            Assert.True(team.IsApproved);
            Assert.Equal(setup.Coach.Id, team.CoachId);
            Assert.True(await db.UserClaims.AnyAsync(claim => claim.UserId == id && claim.ClaimType == ClaimNames.IcpcTeamId && claim.ClaimValue == "12345", token));
            await scope.ServiceProvider.GetRequiredService<CompetitorManagementService>().GenerateLoginsAsync(setup.Actor, setup.Contest.Id, token);
            await db.Competitors.Where(item => item.Id == id).ExecuteUpdateAsync(update => update
                .SetProperty(item => item.IsOutOfCompetition, true).SetProperty(item => item.IsDisqualified, true), token);
        }
        string? login, password;
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var team = await db.Competitors.SingleAsync(item => item.Id == id, token);
            login = team.UserName;
            password = team.PasswordHash;
            var source = setup.Source with { Name = "Renamed team", Status = "PENDING" };
            var service = scope.ServiceProvider.GetRequiredService<IcpcImportService>();
            var preview = await service.PreviewAsync(setup.Actor, setup.Contest.Id, [source], token);
            Assert.Equal(id, Assert.Single(preview.Rows).ExistingId);
            Assert.Equal(new IcpcImportSummary(0, 1), await service.ImportAsync(setup.Actor, setup.Contest.Id, preview.Rows, token));
        }
        using var final = site.Services.CreateScope();
        var saved = await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors.Include(item => item.Members)
            .SingleAsync(item => item.ContestId == setup.Contest.Id, token);
        Assert.Equal(id, saved.Id);
        Assert.Equal("Renamed team", saved.Name);
        Assert.False(saved.IsApproved);
        Assert.True(saved.IsOutOfCompetition);
        Assert.True(saved.IsDisqualified);
        Assert.Equal(login, saved.UserName);
        Assert.Equal(password, saved.PasswordHash);
        Assert.Equal(setup.Member.Id, Assert.Single(saved.Members).Id);
        Assert.Equal("Alex Smith", Assert.Single(saved.GetMembers()!).name);
    }

    [Fact]
    public async Task MissingCoachAndMembersProduceWarningsWithoutCreatingPermanentAccounts()
    {
        var token = TestContext.Current.CancellationToken;
        var setup = await SetupAsync();
        var source = setup.Source with
        {
            Members = [new() { TeamId = 12345, FirstName = "Unknown", LastName = "Contestant", Username = "missing@example.test", Role = "CONTESTANT" }]
        };
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        int count = await db.OlympUsers.CountAsync(token);
        var service = scope.ServiceProvider.GetRequiredService<IcpcImportService>();
        var preview = await service.PreviewAsync(setup.Actor, setup.Contest.Id, [source], token);
        var row = Assert.Single(preview.Rows);
        Assert.Null(row.CoachId);
        Assert.Null(Assert.Single(row.Members).UserId);
        Assert.True(row.Warnings.Count >= 2);
        await service.ImportAsync(setup.Actor, setup.Contest.Id, preview.Rows, token);
        Assert.Equal(count, await db.OlympUsers.CountAsync(token));
        var team = await db.Competitors.Include(item => item.Members).SingleAsync(item => item.ContestId == setup.Contest.Id, token);
        Assert.Null(team.CoachId);
        Assert.Empty(team.Members);
        Assert.Equal("Unknown Contestant", Assert.Single(team.GetMembers()!).name);
    }

    [Fact]
    public async Task RequiresStoredContestManagementPermissionAtPreviewAndSave()
    {
        var token = TestContext.Current.CancellationToken;
        var setup = await SetupAsync();
        var other = await site.AddUserAsync(RoleNames.Jury);
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IcpcImportService>();
        await Assert.ThrowsAsync<OperationValidationException>(() => service.PreviewAsync(Actor(other), setup.Contest.Id, [setup.Source], token));
        var preview = await service.PreviewAsync(setup.Actor, setup.Contest.Id, [setup.Source], token);
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        int ownerId = int.Parse(setup.Actor.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await db.UserClaims.Where(claim => claim.UserId == ownerId && claim.ClaimType == ClaimNames.ContestManage)
            .ExecuteDeleteAsync(token);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.ImportAsync(setup.Actor, setup.Contest.Id, preview.Rows, token));
        Assert.False(await db.Competitors.AnyAsync(item => item.ContestId == setup.Contest.Id, token));
    }

    [Fact]
    public async Task RejectsStalePreviewAndDoesNotOverwriteOtherEditorsChanges()
    {
        var token = TestContext.Current.CancellationToken;
        var setup = await SetupAsync();
        using (var scope = site.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IcpcImportService>();
            var preview = await service.PreviewAsync(setup.Actor, setup.Contest.Id, [setup.Source], token);
            await service.ImportAsync(setup.Actor, setup.Contest.Id, preview.Rows, token);
        }
        using var edit = site.Services.CreateScope();
        var importer = edit.ServiceProvider.GetRequiredService<IcpcImportService>();
        var stale = await importer.PreviewAsync(setup.Actor, setup.Contest.Id, [setup.Source], token);
        using (var change = site.Services.CreateScope())
            await change.ServiceProvider.GetRequiredService<OlympContext>().Competitors.Where(item => item.ContestId == setup.Contest.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Name, "Changed by another editor"), token);
        await Assert.ThrowsAsync<OperationValidationException>(() => importer.ImportAsync(setup.Actor, setup.Contest.Id, stale.Rows, token));
        Assert.Equal("Changed by another editor", await edit.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .Where(item => item.ContestId == setup.Contest.Id).Select(item => item.Name).SingleAsync(token));
    }

    [Fact]
    public async Task RejectsNonIcpcContestAndDuplicateMembershipWithoutPartialImport()
    {
        var token = TestContext.Current.CancellationToken;
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IcpcImportService>();
        var other = setup.Source with { Id = 54321, Name = "Other team" };
        var preview = await service.PreviewAsync(setup.Actor, setup.Contest.Id, [setup.Source, other], token);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.ImportAsync(setup.Actor, setup.Contest.Id, preview.Rows, token));
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False(await db.Competitors.AnyAsync(item => item.ContestId == setup.Contest.Id, token));
        await db.Contests.Where(item => item.Id == setup.Contest.Id).ExecuteUpdateAsync(update => update.SetProperty(item => item.Abbr, "acm" + setup.Contest.Abbr), token);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.PreviewAsync(setup.Actor, setup.Contest.Id, [setup.Source], token));
    }

    [Fact]
    public async Task MultipleMatchedCoachesRequireSelectionAndCanceledTeamsAreNotSelected()
    {
        var token = TestContext.Current.CancellationToken;
        var setup = await SetupAsync();
        var otherCoach = await site.AddUserAsync();
        var source = setup.Source with
        {
            Status = "CANCELED",
            Members = [.. setup.Source.Members, new() { TeamId = 12345, Role = "COACH", FirstName = "Other", Username = otherCoach.Email }]
        };
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IcpcImportService>();
        var preview = await service.PreviewAsync(setup.Actor, setup.Contest.Id, [source], token);
        var row = Assert.Single(preview.Rows);
        Assert.False(row.Selected);
        Assert.Null(row.CoachId);
        row.Selected = true;
        await Assert.ThrowsAsync<OperationValidationException>(() => service.ImportAsync(setup.Actor, setup.Contest.Id, preview.Rows, token));
        row.CoachId = otherCoach.Id;
        Assert.Equal(new IcpcImportSummary(1, 0), await service.ImportAsync(setup.Actor, setup.Contest.Id, preview.Rows, token));
    }

    [Fact]
    public async Task IcpcManualRegistrationAllowsOnlyOutOfCompetition()
    {
        var token = TestContext.Current.CancellationToken;
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Contests.Where(item => item.Id == setup.Contest.Id).ExecuteUpdateAsync(update => update.SetProperty(item => item.IsHidden, false), token);
        Assert.True(setup.Contest.IsAcm);
        Assert.True(setup.Contest.IsIcpc);
        var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
        TeamMember[] members = [new(null, "One"), new(null, "Two"), new(null, "Three")];
        await Assert.ThrowsAsync<OperationValidationException>(() => service.RegisterTeamAsync(setup.Contest.Id, setup.OrgId, setup.Coach.Id, "Manual", members, false, token));
        await service.RegisterTeamAsync(setup.Contest.Id, setup.OrgId, setup.Coach.Id, "Manual", members, true, token);
        var team = await db.Competitors.SingleAsync(item => item.ContestId == setup.Contest.Id, token);
        Assert.True(team.IsOutOfCompetition);
    }

    private async Task<(ClaimsPrincipal Actor, Contest Contest, OlympUser Member, OlympUser Coach, int OrgId, IcpcTeam Source)> SetupAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var member = await site.AddUserAsync();
        var coach = await site.AddUserAsync();
        var contest = await site.AddContestAsync(owner, "icpc");
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var org = new Organization { Name = "University " + Guid.NewGuid().ToString("N"), Users = [] };
        db.Organizations.Add(org);
        await db.SaveChangesAsync(token);
        var source = new IcpcTeam
        {
            Id = 12345,
            Name = "Imported team",
            InstName = org.Name,
            Status = "ACCEPTED",
            EligibilityIssue = "An eligibility detail does not change the registration status.",
            Members =
            [
                new() { TeamId = 12345, Role = "CONTESTANT", FirstName = "Alex", LastName = "Smith", Username = member.Email!.ToUpperInvariant() },
                new() { TeamId = 12345, Role = "COACH", FirstName = "Coach", Username = coach.Email }
            ]
        };
        return (Actor(owner), contest, member, coach, org.Id, source);
    }

    private static ClaimsPrincipal Actor(OlympUser user) => new(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Role, RoleNames.Jury)], "Test"));

    private sealed class FailSecondTeam : SaveChangesInterceptor
    {
        public int FirstId { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FirstId != 0 && eventData.Context!.ChangeTracker.Entries<Competitor>().Any(entry => entry.State == EntityState.Added))
                throw new InvalidOperationException("Simulated save failure.");
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            FirstId = eventData.Context!.ChangeTracker.Entries<Competitor>().FirstOrDefault()?.Entity.Id ?? FirstId;
            return ValueTask.FromResult(result);
        }
    }
}

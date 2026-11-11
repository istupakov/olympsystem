using System.Security.Claims;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

[Collection("Contest management")]
public class CompetitorCancellationTests(ContestServiceFixture site) : IClassFixture<ContestServiceFixture>
{
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    public async Task IndividualCancellationRequiresMembership(bool member, bool coach, bool approved, bool allowed)
    {
        var (user, participant) = await SetupAsync();
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var stored = await db.Competitors.AsTracking().Include(item => item.Members)
                .SingleAsync(item => item.Id == participant.Id, TestContext.Current.CancellationToken);
            if (!member)
                stored.Members.Clear();
            stored.CoachId = coach ? user.Id : null;
            stored.IsApproved = approved;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await AssertCancellationAsync(await PrincipalAsync(user), participant, allowed);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public async Task TeamCancellationRequiresOwnCoach(bool coach, bool member, bool allowed)
    {
        var (user, participant) = await SetupAsync(team: true);
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var stored = await db.Competitors.AsTracking().Include(item => item.Members)
                .SingleAsync(item => item.Id == participant.Id, TestContext.Current.CancellationToken);
            stored.CoachId = coach ? user.Id : null;
            if (!member)
                stored.Members.Clear();
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await AssertCancellationAsync(await PrincipalAsync(user), participant, allowed);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public async Task TeamCancellationRespectsApproval(bool approved, bool unofficial, bool allowed)
    {
        var (user, participant) = await SetupAsync(team: true);
        using (var scope = site.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors.Where(item => item.Id == participant.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsApproved, approved)
                    .SetProperty(item => item.IsOutOfCompetition, unofficial), TestContext.Current.CancellationToken);
        await AssertCancellationAsync(await PrincipalAsync(user), participant, allowed);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task TeamCancellationRequiresMatchingOrganizationClaim(int? organizationOffset, bool allowed)
    {
        var (user, participant) = await SetupAsync(team: true);
        var actor = await PrincipalAsync(user);
        var identity = actor.Identities.Single();
        identity.RemoveClaim(identity.FindFirst(ClaimNames.CoachOrg)!);
        if (organizationOffset is int offset)
            identity.AddClaim(new(ClaimNames.CoachOrg, (participant.OrganizationId + offset).ToString()!));
        await AssertCancellationAsync(actor, participant, allowed);
    }

    public enum AccountKind { Guest, Temporary, Legacy }

    [Theory]
    [InlineData(false, AccountKind.Guest)]
    [InlineData(false, AccountKind.Temporary)]
    [InlineData(false, AccountKind.Legacy)]
    [InlineData(true, AccountKind.Guest)]
    [InlineData(true, AccountKind.Temporary)]
    [InlineData(true, AccountKind.Legacy)]
    public async Task CancellationRequiresPermanentMigratedAccount(bool team, AccountKind account)
    {
        var (user, participant) = await SetupAsync(team);
        using var scope = site.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        User actor = user;
        if (account == AccountKind.Temporary)
        {
            actor = participant;
            Assert.True((await users.SetUserNameAsync(actor, "temporary-" + Guid.NewGuid().ToString("N"))).Succeeded);
            Assert.True((await users.AddClaimAsync(actor, new(ClaimNames.CompetitorContest, participant.ContestId.ToString()))).Succeeded);
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            if (team)
            {
                await db.Competitors.Where(item => item.Id == participant.Id).ExecuteUpdateAsync(
                    setters => setters.SetProperty(item => item.CoachId, participant.Id), TestContext.Current.CancellationToken);
                Assert.True((await users.AddClaimAsync(actor, new(ClaimNames.CoachOrg, participant.OrganizationId!.Value.ToString()))).Succeeded);
            }
        }
        if (account == AccountKind.Legacy)
            await scope.ServiceProvider.GetRequiredService<OlympContext>().Users.Where(item => item.Id == user.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NormalizedEmail, (string?)null), TestContext.Current.CancellationToken);
        var principal = await PrincipalAsync(actor);
        if (account == AccountKind.Guest)
            principal = new ClaimsPrincipal(new ClaimsIdentity(principal.Claims));
        await AssertCancellationAsync(principal, participant, allowed: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenContestPreventsCancellation(bool team)
    {
        var (user, participant) = await SetupAsync(team);
        using (var scope = site.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<OlympContext>().Contests.Where(item => item.Id == participant.ContestId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsHidden, true), TestContext.Current.CancellationToken);
        await AssertCancellationAsync(await PrincipalAsync(user), participant, allowed: false);
    }

    [Theory]
    [InlineData(false, -1, true)]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(true, -1, true)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, false)]
    public async Task CancellationStopsExactlyAtStart(bool team, int ticksFromStart, bool allowed)
    {
        var (user, participant) = await SetupAsync(team);
        var clock = new ManualTimeProvider(participant.Contest.StartTime.AddTicks(ticksFromStart));
        using var host = site.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var scope = host.Services.CreateScope();
        await AssertCancellationAsync(await PrincipalAsync(user), participant, allowed, scope.ServiceProvider);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DeletionRetainsParticipantMembershipAndHistory(bool cancellation, bool submission)
    {
        var (user, participant) = await SetupAsync();
        await AddHistoryAsync(participant, submission);
        var actor = await PrincipalAsync(cancellation ? user : await site.AddUserAsync(RoleNames.Admin));
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
        var token = TestContext.Current.CancellationToken;
        var exception = await Assert.ThrowsAsync<OperationValidationException>(() => cancellation
            ? service.CancelRegistrationAsync(actor, participant.ContestId, participant.Id, token)
            : service.DeleteAsync(actor, participant.ContestId, participant.Id, token));
        Assert.Equal(SiteResources.ParticipantDeletionHasHistory, exception.Message);
        await AssertHistoryAsync(participant, user, submission);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupSkipsParticipantsWithHistory(bool submission)
    {
        var (user, participant) = await SetupAsync();
        await AddHistoryAsync(participant, submission);
        var empty = TestEntities.CreateCompetitor(participant.Contest, "Empty");
        using (var setup = site.Services.CreateScope())
        {
            empty.Contest = null!;
            Assert.True((await setup.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(empty)).Succeeded);
            await setup.ServiceProvider.GetRequiredService<OlympContext>().Contests.Where(item => item.Id == participant.ContestId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.EndTime, DateTimeOffset.Now.AddHours(-1)), TestContext.Current.CancellationToken);
        }
        using var scope = site.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<CompetitorManagementService>().DeleteWithoutSubmissionsAsync(
            await PrincipalAsync(await site.AddUserAsync(RoleNames.Admin)), participant.ContestId, TestContext.Current.CancellationToken));
        Assert.False(await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors.AnyAsync(item => item.Id == empty.Id, TestContext.Current.CancellationToken));
        await AssertHistoryAsync(participant, user, submission);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task CleanupRequiresStrictlyAfterEnd(int ticksFromEnd, bool allowed)
    {
        var (_, participant) = await SetupAsync();
        var actor = await PrincipalAsync(await site.AddUserAsync(RoleNames.Admin));
        var clock = new ManualTimeProvider(participant.Contest.EndTime.AddTicks(ticksFromEnd));
        using var host = site.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
        var token = TestContext.Current.CancellationToken;
        if (allowed)
            Assert.Equal(1, await service.DeleteWithoutSubmissionsAsync(actor, participant.ContestId, token));
        else
            Assert.Equal(SiteResources.ParticipantCleanupAfterEndOnly,
                (await Assert.ThrowsAsync<OperationValidationException>(() => service.DeleteWithoutSubmissionsAsync(actor, participant.ContestId, token))).Message);
        using var verification = site.Services.CreateScope();
        Assert.Equal(!allowed, await verification.ServiceProvider.GetRequiredService<OlympContext>().Competitors.AnyAsync(item => item.Id == participant.Id, token));
    }

    private async Task<(OlympUser User, Competitor Participant)> SetupAsync(bool team = false)
    {
        var token = TestContext.Current.CancellationToken;
        var user = await site.AddUserAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Cancellation", DateTimeOffset.Now.AddHours(1), DateTimeOffset.Now.AddHours(4));
        contest.Abbr = (team ? "acm" : "school") + Guid.NewGuid().ToString("N");
        db.Attach(user);
        var participant = TestEntities.CreateCompetitor(contest, "Participant");
        participant.SecurityStamp = Guid.NewGuid().ToString();
        participant.IsApproved = false;
        participant.Members.Add(user);
        participant.Organization = new Organization { Name = "Cancellation", Users = [] };
        participant.CoachId = team ? user.Id : null;
        db.Competitors.Add(participant);
        await db.SaveChangesAsync(token);
        if (team)
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().AddClaimAsync(user,
                new(ClaimNames.CoachOrg, participant.OrganizationId!.Value.ToString()))).Succeeded);
        return (user, participant);
    }

    private async Task<ClaimsPrincipal> PrincipalAsync(User user)
    {
        using var scope = site.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<User>>().CreateAsync(user);
    }

    private async Task AssertCancellationAsync(ClaimsPrincipal actor, Competitor participant, bool allowed, IServiceProvider? services = null)
    {
        using var scope = site.Services.CreateScope();
        var service = (services ?? scope.ServiceProvider).GetRequiredService<CompetitorManagementService>();
        var token = TestContext.Current.CancellationToken;
        if (allowed)
            await service.CancelRegistrationAsync(actor, participant.ContestId, participant.Id, token);
        else
            await Assert.ThrowsAsync<OperationValidationException>(() => service.CancelRegistrationAsync(actor, participant.ContestId, participant.Id, token));
        Assert.Equal(!allowed, await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors.AnyAsync(item => item.Id == participant.Id, token));
    }

    private async Task AddHistoryAsync(Competitor participant, bool submission)
    {
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        if (submission)
        {
            var problem = TestEntities.CreateProblem(participant.Contest);
            problem.CheckerId = 4;
            problem.Checker = null!;
            problem.Contest = null!;
            var item = TestEntities.CreateSubmission(participant, problem, TestEntities.CreateCompilator(), DateTimeOffset.Now);
            item.User = null!;
            db.Submissions.Add(item);
        }
        else
            db.Messages.Add(new Message { ContestId = participant.ContestId, Contest = null!, UserId = participant.Id, UserText = "Question" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task AssertHistoryAsync(Competitor participant, OlympUser user, bool submission)
    {
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var token = TestContext.Current.CancellationToken;
        Assert.True(await db.Competitors.AnyAsync(item => item.Id == participant.Id && item.Members.Any(member => member.Id == user.Id), token));
        Assert.True(await db.OlympUsers.AnyAsync(item => item.Id == user.Id, token));
        Assert.Equal(submission ? 1 : 0, await db.Submissions.CountAsync(item => item.UserId == participant.Id, token));
        Assert.Equal(submission ? 0 : 1, await db.Messages.CountAsync(item => item.UserId == participant.Id, token));
    }
}

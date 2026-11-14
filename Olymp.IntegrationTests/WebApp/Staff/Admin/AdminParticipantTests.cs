using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Admin;

[Trait("Category", "Integration")]
public class AdminParticipantTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Fact]
    public async Task OrganizationAndRosterEditsPreserveAccountCoachAndMembership()
    {
        var admin = await factory.AddUserAsync();
        var member = await factory.AddUserAsync();
        int id, organizationId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var participant = TestEntities.CreateCompetitor(TestEntities.CreateContest("Roster", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1)), "Team");
            participant.CoachId = admin.Id;
            participant.Members.Add((await db.OlympUsers.FindAsync([member.Id], TestContext.Current.CancellationToken))!);
            participant.MemberInfos = $"Snapshot;{member.Id};Unlinked;";
            var organization = new Organization { Name = "New organization", Users = [] };
            db.Add(organization);
            db.Add(participant);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            id = participant.Id;
            organizationId = organization.Id;
        }
        var actor = Actor(admin, RoleNames.Admin);
        await ChangeAsync(service => service.SetOrganizationAsync(actor, id, organizationId));
        await ChangeAsync(service => service.SetOrganizationAsync(actor, id, null));
        await ChangeAsync(service => service.SetOrganizationAsync(actor, id, organizationId));
        await ChangeAsync(service => service.RenameRosterEntryAsync(actor, id, 0, " Display name "));
        await ChangeAsync(service => service.AddRosterEntryAsync(actor, id, "Third person"));
        await Assert.ThrowsAsync<OperationValidationException>(() => ChangeAsync(service => service.AddRosterEntryAsync(actor, id, "Fourth")));
        await Assert.ThrowsAsync<OperationValidationException>(() => ChangeAsync(service => service.RemoveRosterEntryAsync(actor, id, 0)));
        await Assert.ThrowsAsync<OperationValidationException>(() => ChangeAsync(service => service.RenameRosterEntryAsync(actor, id, 0, "Invalid;name")));
        await Assert.ThrowsAsync<OperationValidationException>(() => ChangeAsync(service => service.SetOrganizationAsync(actor, id, int.MaxValue)));
        await ChangeAsync(service => service.RenameRosterEntryAsync(actor, id, 1, "Unlinked renamed"));
        await ChangeAsync(service => service.RemoveRosterEntryAsync(actor, id, 2));

        await using var verify = factory.Services.CreateAsyncScope();
        var context = verify.ServiceProvider.GetRequiredService<OlympContext>();
        var saved = await context.Competitors.Include(item => item.Members).SingleAsync(item => item.Id == id, TestContext.Current.CancellationToken);
        Assert.Equal(organizationId, saved.OrganizationId);
        Assert.Equal(admin.Id, saved.CoachId);
        Assert.Equal(member.Id, Assert.Single(saved.Members).Id);
        Assert.Equal(member.Name, saved.Members.Single().Name);
        Assert.Equal($"Display name;{member.Id};Unlinked renamed;", saved.MemberInfos);
        Assert.Equal("Display name", saved.GetMembers()!.First().name);
    }

    [Fact]
    public async Task AdminEditsFinishedParticipantAcrossOrganizationsAndRetainsUnlinkedRosterNames()
    {
        var admin = await factory.AddUserAsync();
        var member = await factory.AddUserAsync();
        var coach = await factory.AddUserAsync();
        int participantId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var contest = TestEntities.CreateContest("Finished", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
            var participant = TestEntities.CreateCompetitor(contest, "Original");
            participant.MemberInfos = "Roster name;;Unlinked member;";
            var organization = new Organization { Name = "Participant organization", Users = [] };
            participant.Organization = organization;
            db.Competitors.Add(participant);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            participantId = participant.Id;
        }
        var actor = Actor(admin, RoleNames.Admin);
        await ChangeAsync(service => service.RenameAsync(actor, participantId, " Renamed "));
        await ChangeAsync(service => service.SetCoachAsync(actor, participantId, coach.Id));
        await ChangeAsync(service => service.AddMemberAsync(actor, participantId, member.Id, 0));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var saved = await db.Competitors.Include(item => item.Members).SingleAsync(item => item.Id == participantId, TestContext.Current.CancellationToken);
            Assert.Equal("Renamed", saved.Name);
            Assert.Equal(coach.Id, saved.CoachId);
            Assert.Equal(member.Id, Assert.Single(saved.Members).Id);
            Assert.Equal($"Roster name;{member.Id};Unlinked member;", saved.MemberInfos);
            Assert.NotNull(saved.OrganizationId);
            Assert.Null(member.OrganizationId);
        }
        await ChangeAsync(service => service.RemoveMemberAsync(actor, participantId, member.Id));
        await ChangeAsync(service => service.SetCoachAsync(actor, participantId, null));
        await using var verify = factory.Services.CreateAsyncScope();
        var context = verify.ServiceProvider.GetRequiredService<OlympContext>();
        var retained = await context.Competitors.Include(item => item.Members).SingleAsync(item => item.Id == participantId, TestContext.Current.CancellationToken);
        Assert.Empty(retained.Members);
        Assert.Null(retained.CoachId);
        Assert.Equal("Roster name;;Unlinked member;", retained.MemberInfos);
    }

    [Fact]
    public async Task DuplicateParticipationIsRejectedWithoutChangingRoster()
    {
        var admin = await factory.AddUserAsync();
        var member = await factory.AddUserAsync();
        int firstId, secondId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var contest = TestEntities.CreateContest("Duplicate", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
            var first = TestEntities.CreateCompetitor(contest, "First");
            var second = TestEntities.CreateCompetitor(contest, "Second");
            second.MemberInfos = "Existing name;";
            db.Competitors.AddRange(first, second);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            firstId = first.Id;
            secondId = second.Id;
        }
        var actor = Actor(admin, RoleNames.Admin);
        await ChangeAsync(service => service.AddMemberAsync(actor, firstId, member.Id, null));
        await Assert.ThrowsAsync<OperationValidationException>(() => ChangeAsync(service => service.AddMemberAsync(actor, secondId, member.Id, 0)));
        await using var verify = factory.Services.CreateAsyncScope();
        var dbVerify = verify.ServiceProvider.GetRequiredService<OlympContext>();
        var saved = await dbVerify.Competitors.Include(item => item.Members).SingleAsync(item => item.Id == secondId, TestContext.Current.CancellationToken);
        Assert.Empty(saved.Members);
        Assert.Equal("Existing name;", saved.MemberInfos);
    }

    [Theory]
    [InlineData(RoleNames.Jury)]
    [InlineData(null)]
    public async Task DirectMutationsRequireAdmin(string? role)
    {
        var user = await factory.AddUserAsync();
        var member = await factory.AddUserAsync();
        int participantId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var participant = TestEntities.CreateCompetitor(TestEntities.CreateContest("Access", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)), "Original");
            db.Competitors.Add(participant);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            participantId = participant.Id;
        }
        var actor = Actor(user, role);
        await Assert.ThrowsAsync<OperationValidationException>(() => ChangeAsync(service => service.SetOrganizationAsync(actor, participantId, null)));
        await Assert.ThrowsAsync<OperationValidationException>(() => ChangeAsync(service => service.RenameRosterEntryAsync(actor, participantId, 0, "Changed")));
        await Assert.ThrowsAsync<OperationValidationException>(() => ChangeAsync(service => service.AddRosterEntryAsync(actor, participantId, "Changed")));
        await Assert.ThrowsAsync<OperationValidationException>(() => ChangeAsync(service => service.RemoveRosterEntryAsync(actor, participantId, 0)));
        await Assert.ThrowsAsync<OperationValidationException>(() => ChangeAsync(service => service.RenameAsync(actor, participantId, "Changed")));
        await Assert.ThrowsAsync<OperationValidationException>(() => ChangeAsync(service => service.SetCoachAsync(actor, participantId, user.Id)));
        await Assert.ThrowsAsync<OperationValidationException>(() => ChangeAsync(service => service.AddMemberAsync(actor, participantId, member.Id, null)));
        await Assert.ThrowsAsync<OperationValidationException>(() => ChangeAsync(service => service.RemoveMemberAsync(actor, participantId, member.Id)));
        await using var verify = factory.Services.CreateAsyncScope();
        var saved = await verify.ServiceProvider.GetRequiredService<OlympContext>().Competitors.SingleAsync(item => item.Id == participantId, TestContext.Current.CancellationToken);
        Assert.Equal("Original", saved.Name);
        Assert.Null(saved.CoachId);
    }

    private async Task ChangeAsync(Func<AdminParticipantService, Task> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await change(scope.ServiceProvider.GetRequiredService<AdminParticipantService>());
    }

    private static ClaimsPrincipal Actor(OlympUser user, string? role)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id.ToString()) };
        if (role is not null) claims.Add(new(ClaimTypes.Role, role));
        return new(new ClaimsIdentity(claims, "Test"));
    }
}

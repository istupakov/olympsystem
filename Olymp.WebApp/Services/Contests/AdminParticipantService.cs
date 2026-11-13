using System.Data;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Contests;

public sealed class AdminParticipantService(OlympContext context, IAuthorizationService authorization, UserManager<User> users)
{
    public Task RenameAsync(ClaimsPrincipal actor, int id, string name, CancellationToken token = default) => ChangeAsync(actor, id, async participant =>
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new OperationValidationException(SiteResources.StaffParticipantNameRequired);
        if (await context.Competitors.AnyAsync(item => item.ContestId == participant.ContestId && item.Id != id && item.Name == name, token))
            throw new OperationValidationException(SiteResources.TeamNameAlreadyRegistered);
        participant.Name = name;
    }, token);

    public Task SetCoachAsync(ClaimsPrincipal actor, int id, int? coachId, CancellationToken token = default) => ChangeAsync(actor, id, async participant =>
    {
        if (coachId.HasValue && !await context.OlympUsers.AnyAsync(item => item.Id == coachId, token))
            throw new OperationValidationException(SiteResources.InvalidParticipantValue);
        participant.CoachId = coachId;
    }, token);

    public Task AddMemberAsync(ClaimsPrincipal actor, int id, int memberId, int? slot, CancellationToken token = default) => ChangeAsync(actor, id, async participant =>
    {
        var account = await context.OlympUsers.SingleOrDefaultAsync(item => item.Id == memberId, token)
            ?? throw new OperationValidationException(SiteResources.InvalidParticipantValue);
        if (await context.Competitors.AnyAsync(item => item.ContestId == participant.ContestId && item.Members.Any(member => member.Id == memberId), token))
            throw new OperationValidationException(SiteResources.ParticipantsAlreadyRegistered);
        var members = ReadRoster(participant);
        if (slot is int index)
        {
            if (index < 0 || index >= members.Count || members[index].Id.HasValue)
                throw new OperationValidationException(SiteResources.InvalidParticipantValue);
            members[index] = (members[index].Name, memberId);
        }
        else
        {
            if (members.Count >= 3 || account.Name.Contains(';'))
                throw new OperationValidationException(SiteResources.StaffRosterFull);
            members.Add((account.Name, memberId));
        }
        participant.Members.Add(account);
        participant.MemberInfos = WriteRoster(members);
    }, token);

    public Task RemoveMemberAsync(ClaimsPrincipal actor, int id, int memberId, CancellationToken token = default) => ChangeAsync(actor, id, participant =>
    {
        var member = participant.Members.SingleOrDefault(item => item.Id == memberId)
            ?? throw new OperationValidationException(SiteResources.InvalidParticipantValue);
        var members = ReadRoster(participant);
        for (int index = 0; index < members.Count; index++)
            if (members[index].Id == memberId) members[index] = (members[index].Name, null);
        participant.Members.Remove(member);
        participant.MemberInfos = WriteRoster(members);
        return Task.CompletedTask;
    }, token);

    public Task SetLoginBlockedAsync(ClaimsPrincipal actor, int id, bool blocked, CancellationToken token = default) => ChangeAsync(actor, id, async participant =>
    {
        Check(await users.SetLockoutEnabledAsync(participant, true));
        Check(await users.SetLockoutEndDateAsync(participant, blocked ? DateTimeOffset.Now.AddYears(100) : null));
        if (blocked) Check(await users.UpdateSecurityStampAsync(participant));
    }, token);

    private static void Check(IdentityResult result) { if (!result.Succeeded) throw new IdentityOperationException(result.Errors); }

    private async Task ChangeAsync(ClaimsPrincipal actor, int id, Func<Competitor, Task> change, CancellationToken token)
    {
        if (!(await authorization.AuthorizeAsync(actor, PolicyNames.Admin)).Succeeded)
            throw new OperationValidationException(SiteResources.StaffAccessDenied);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var participant = await context.Competitors.Include(item => item.Members).SingleOrDefaultAsync(item => item.Id == id, token)
            ?? throw new EntityNotFoundException();
        await change(participant);
        await context.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    private static List<(string Name, int? Id)> ReadRoster(Competitor participant)
    {
        if (string.IsNullOrEmpty(participant.MemberInfos))
            return [.. participant.Members.Select(member => (member.Name, (int?)member.Id))];
        var parts = participant.MemberInfos.Split(';');
        if (parts.Length is not (2 or 4 or 6))
            throw new OperationValidationException(SiteResources.RegistrationInformationIncorrect);
        return [.. parts.Chunk(2).Select(part => (part[0], int.TryParse(part[1], out int id) ? (int?)id : null))];
    }

    private static string? WriteRoster(List<(string Name, int? Id)> members) => members.Count == 0 ? null
        : string.Join(";", members.Select(member => $"{member.Name};{member.Id}"));
}

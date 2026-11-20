using System.Data;
using System.Globalization;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Identity;

public sealed class AccountMergeService(OlympContext context, UserManager<User> users,
    SignInManager<User> signIn, IAuthorizationService authorization)
{
    public async Task MergeAsync(ClaimsPrincipal actor, string login, string password, CancellationToken token = default)
    {
        if (!(await authorization.AuthorizeAsync(actor, PolicyNames.PermanentAccount)).Succeeded
            || await users.GetUserAsync(actor) is not OlympUser target)
            throw new UnauthorizedAccessException();

        var source = await users.FindByNameAsync(login) ?? await users.FindByEmailAsync(login);
        if (source is not OlympUser || source.Id == target.Id)
            throw new OperationValidationException(AccountResources.MergeInvalidAccount);
        if (!(await signIn.CheckPasswordSignInAsync(source, password, lockoutOnFailure: true)).Succeeded
            || source.TwoFactorEnabled)
            throw new OperationValidationException(AccountResources.InvalidLoginAttempt);

        string? sourceStamp = source.SecurityStamp;
        string? sourceHash = source.PasswordHash;
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await context.Entry(source).ReloadAsync(token);
        await context.Entry(target).ReloadAsync(token);
        if (context.Entry(source).State == EntityState.Detached || context.Entry(target).State == EntityState.Detached
            || source.SecurityStamp != sourceStamp || source.PasswordHash != sourceHash
            || !await signIn.CanSignInAsync(source) || await users.IsLockedOutAsync(source) || source.TwoFactorEnabled)
            throw new OperationValidationException(AccountResources.InvalidLoginAttempt);

        var participants = await context.Competitors.AsTracking().Include(item => item.Members).Include(item => item.Contest)
            .Where(item => item.Members.Any(member => member.Id == source.Id || member.Id == target.Id))
            .ToListAsync(token);
        var conflicts = participants.GroupBy(item => item.ContestId).Where(group => group.Count() > 1)
            .Select(group => group.First().Contest.Name).Order().ToArray();
        if (conflicts.Length > 0)
            throw new OperationValidationException(string.Format(CultureInfo.CurrentCulture,
                AccountResources.MergeParticipationConflict, string.Join(", ", conflicts)));

        foreach (var participant in participants)
        {
            var oldMember = participant.Members.SingleOrDefault(member => member.Id == source.Id);
            if (oldMember is null) continue;
            participant.Members.Remove(oldMember);
            if (!participant.Members.Any(member => member.Id == target.Id)) participant.Members.Add(target);
        }

        // Roster names describe the historical team; only account references change.
        string oldId = source.Id.ToString(CultureInfo.InvariantCulture);
        string newId = target.Id.ToString(CultureInfo.InvariantCulture);
        var rosters = await context.Competitors.AsTracking()
            .Where(item => item.MemberInfos != null && item.MemberInfos.Contains(oldId)).ToListAsync(token);
        foreach (var roster in rosters)
        {
            var parts = roster.MemberInfos!.Split(';');
            bool changed = false;
            for (int index = 1; index < parts.Length; index += 2)
            {
                if (!int.TryParse(parts[index], out int id) || id != source.Id) continue;
                parts[index] = newId;
                changed = true;
            }
            if (changed) roster.MemberInfos = string.Join(';', parts);
        }
        var coachedAccounts = await context.Users.AsTracking()
            .Where(item => item.CoachId == source.Id && item.Id != source.Id).ToListAsync(token);
        foreach (var account in coachedAccounts)
            account.Coach = account.Id == target.Id ? null : target;
        await context.SaveChangesAsync(token);

        await context.Submissions.Where(item => item.UserId == source.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UserId, target.Id), token);
        await context.Messages.Where(item => item.UserId == source.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UserId, target.Id), token);
        var result = await users.DeleteAsync(source);
        if (!result.Succeeded) throw new IdentityOperationException(result.Errors);
        await transaction.CommitAsync(token);
    }
}

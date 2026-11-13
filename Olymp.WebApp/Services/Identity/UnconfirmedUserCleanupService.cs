using System.Data;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.WebApp.Services.Identity;

public sealed class UnconfirmedUserCleanupService(OlympContext context, UserManager<User> users,
    IAuthorizationService authorization, TimeProvider clock)
{
    public sealed record Preview(DateTimeOffset RegisteredBefore, int[] Ids);
    public sealed record Result(int Deleted, int Skipped);

    public async Task<Preview> PrepareAsync(ClaimsPrincipal actor, CancellationToken token = default)
    {
        await AuthorizeAsync(actor);
        var cutoff = clock.GetUtcNow().AddMonths(-1);
        return new(cutoff, await Candidates(cutoff).OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync(token));
    }

    public async Task<Result> DeleteAsync(ClaimsPrincipal actor, Preview preview, CancellationToken token = default)
    {
        await AuthorizeAsync(actor);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var accounts = await Candidates(preview.RegisteredBefore).AsTracking()
            .Where(item => preview.Ids.Contains(item.Id)).OrderBy(item => item.Id).ToListAsync(token);
        foreach (var account in accounts)
        {
            token.ThrowIfCancellationRequested();
            var result = await users.DeleteAsync(account);
            if (!result.Succeeded) throw new IdentityOperationException(result.Errors);
        }
        await transaction.CommitAsync(token);
        return new(accounts.Count, preview.Ids.Length - accounts.Count);
    }

    private IQueryable<OlympUser> Candidates(DateTimeOffset cutoff) => context.OlympUsers.AsNoTracking()
        .Where(item => !item.EmailConfirmed && item.RegistrationDate != null && item.RegistrationDate < cutoff
            && !item.Submissions.Any() && !item.Messages.Any() && !item.Memberships.Any() && !item.Teams.Any()
            && !context.UserRoles.Any(role => role.UserId == item.Id)
            && !context.UserClaims.Any(claim => claim.UserId == item.Id
                && (claim.ClaimType == ClaimNames.CoachOrg || claim.ClaimType == ClaimNames.ContestManage)));

    private async Task AuthorizeAsync(ClaimsPrincipal actor)
    {
        if (!(await authorization.AuthorizeAsync(actor, PolicyNames.Admin)).Succeeded) throw new UnauthorizedAccessException();
    }
}

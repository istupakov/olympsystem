using System.Globalization;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Contests;

public sealed class ContestImportAccessService(OlympContext db, IAuthorizationService authorization)
{
    public sealed record Assignment(int UserId, string Name, string? Email);

    public async Task<List<Assignment>> ListAsync(ClaimsPrincipal actor, int contestId, CancellationToken token = default)
    {
        await AuthorizeAsync(actor, contestId, token);
        string value = contestId.ToString(CultureInfo.InvariantCulture);
        return await db.OlympUsers.Where(user => db.UserClaims.Any(claim => claim.UserId == user.Id
            && claim.ClaimType == ClaimNames.ContestImport && claim.ClaimValue == value))
            .OrderBy(user => user.Name).ThenBy(user => user.Id)
            .Select(user => new Assignment(user.Id, user.Name, user.Email)).ToListAsync(token);
    }

    public async Task SetAsync(ClaimsPrincipal actor, int contestId, int userId, bool granted, CancellationToken token = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token);
        await AuthorizeAsync(actor, contestId, token);
        string value = contestId.ToString(CultureInfo.InvariantCulture);
        if (granted)
        {
            if (!await db.OlympUsers.AnyAsync(user => user.Id == userId, token)
                || !await (from assignment in db.UserRoles
                           join role in db.Roles on assignment.RoleId equals role.Id
                           where assignment.UserId == userId && role.Name == RoleNames.Jury
                           select assignment).AnyAsync(token))
                throw new OperationValidationException(SiteResources.ImportAccessJuryRequired);
            if (!await db.UserClaims.AnyAsync(claim => claim.UserId == userId
                && claim.ClaimType == ClaimNames.ContestImport && claim.ClaimValue == value, token))
            {
                db.UserClaims.Add(new IdentityUserClaim<int> { UserId = userId, ClaimType = ClaimNames.ContestImport, ClaimValue = value });
                await db.SaveChangesAsync(token);
            }
        }
        else
            await db.UserClaims.Where(claim => claim.UserId == userId && claim.ClaimType == ClaimNames.ContestImport
                && claim.ClaimValue == value).ExecuteDeleteAsync(token);
        await transaction.CommitAsync(token);
    }

    private async Task AuthorizeAsync(ClaimsPrincipal actor, int contestId, CancellationToken token)
    {
        if (!(await authorization.AuthorizeAsync(actor, contestId, PolicyNames.ManageContest)).Succeeded)
            throw new OperationValidationException(SiteResources.StaffAccessDenied);
        if (!await db.Contests.AnyAsync(contest => contest.Id == contestId, token)) throw new EntityNotFoundException();
    }
}

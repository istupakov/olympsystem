using System.Globalization;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;

namespace Olymp.WebApp.Services.Authorization;

public sealed class ManageContestRequirement : IAuthorizationRequirement
{
}

internal sealed class ManageContestHandler(IDbContextFactory<OlympContext> contextFactory)
    : AuthorizationHandler<ManageContestRequirement, int>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        ManageContestRequirement requirement, int contestId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        if (await HasAccessAsync(db, context.User, contestId)) context.Succeed(requirement);
    }

    internal static async Task<bool> HasAccessAsync(OlympContext db, ClaimsPrincipal user, int contestId)
    {
        if (user.IsInRole(RoleNames.Admin)) return true;
        if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out int userId)) return false;
        string value = contestId.ToString(CultureInfo.InvariantCulture);
        return await db.UserClaims.AnyAsync(claim => claim.UserId == userId
            && claim.ClaimType == ClaimNames.ContestManage && claim.ClaimValue == value);
    }
}

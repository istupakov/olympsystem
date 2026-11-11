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
        if (context.User.IsInRole(RoleNames.Admin))
        {
            context.Succeed(requirement);
            return;
        }
        if (!int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out int userId))
            return;

        await using var db = await contextFactory.CreateDbContextAsync();
        string value = contestId.ToString(CultureInfo.InvariantCulture);
        if (await db.UserClaims.AnyAsync(claim => claim.UserId == userId
            && claim.ClaimType == ClaimNames.ContestManage && claim.ClaimValue == value))
            context.Succeed(requirement);
    }
}

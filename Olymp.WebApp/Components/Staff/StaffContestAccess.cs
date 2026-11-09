using System.Globalization;
using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Site;
using Olymp.Site.Services.Authorization;

namespace Olymp.WebApp.Components.Staff;

public static class StaffContestAccess
{
    public static async Task<bool> CanManageAsync(ClaimsPrincipal user, int contestId, OlympContext context)
    {
        if (!ContestAccess.CanCreate(user))
            return false;
        if (user.IsInRole(RoleNames.Admin))
            return true;
        if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out int userId))
            return false;

        string value = contestId.ToString(CultureInfo.InvariantCulture);
        return await context.UserClaims.AnyAsync(claim => claim.UserId == userId
            && claim.ClaimType == ClaimNames.ContestManage && claim.ClaimValue == value);
    }
}

using System.Globalization;
using System.Security.Claims;

namespace Olymp.Site.Services.Authorization;

public static class ContestAccess
{
    public static bool CanCreate(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
        && (user.IsInRole(RoleNames.Admin) || user.IsInRole(RoleNames.Jury));

    public static bool CanManage(ClaimsPrincipal user, int contestId) =>
        CanCreate(user) && (user.IsInRole(RoleNames.Admin)
            || user.HasClaim(ClaimNames.ContestManage, contestId.ToString(CultureInfo.InvariantCulture)));
}

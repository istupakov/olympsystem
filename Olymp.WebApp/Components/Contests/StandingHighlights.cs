using System.Security.Claims;

using Olymp.Domain.Models;

namespace Olymp.WebApp.Components.Contests;

public static class StandingHighlights
{
    public static bool IsCoach(IEnumerable<User> users, ClaimsPrincipal viewer) =>
        viewer.Identity?.IsAuthenticated == true
        && (viewer.HasClaim(claim => claim.Type == ClaimNames.CoachOrg)
            || (int.TryParse(viewer.FindFirstValue(ClaimTypes.NameIdentifier), out int userId) && users.Any(user => user.CoachId == userId)));

    public static HashSet<int> GetUserIds(IEnumerable<User> users, ClaimsPrincipal viewer)
    {
        if (viewer.Identity?.IsAuthenticated != true
            || !int.TryParse(viewer.FindFirstValue(ClaimTypes.NameIdentifier), out int userId)) return [];

        return [.. users.Where(user => user.Id == userId || user.CoachId == userId
            || (user is Competitor team && team.Members.Any(member => member.Id == userId)))
            .Select(user => user.Id)];
    }
}

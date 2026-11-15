using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Olymp.Domain;

namespace Olymp.WebApp.Services.Authorization;

internal sealed class ClosedContestRestriction(IDbContextFactory<OlympContext> contextFactory, TimeProvider clock)
{
    public async Task<bool> IsActiveAsync(ClaimsPrincipal actor, CancellationToken token = default)
    {
        if (actor.Identity?.IsAuthenticated != true
            || actor.HasClaim(claim => claim.Type == ClaimNames.CompetitorContest))
            return false;

        var now = clock.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(token);
        return await db.Contests.AnyAsync(contest => !contest.IsHidden && !contest.IsOpen
            && contest.StartTime <= now && now < contest.EndTime, token);
    }
}

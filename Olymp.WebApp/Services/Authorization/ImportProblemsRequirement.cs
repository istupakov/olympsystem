using System.Globalization;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;

namespace Olymp.WebApp.Services.Authorization;

public sealed class ImportProblemsRequirement : IAuthorizationRequirement;

internal sealed class ImportProblemsHandler(IDbContextFactory<OlympContext> contextFactory,
    TimeProvider clock) : AuthorizationHandler<ImportProblemsRequirement, int>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        ImportProblemsRequirement requirement, int contestId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        if (await ManageContestHandler.HasAccessAsync(db, context.User, contestId))
        {
            context.Succeed(requirement);
            return;
        }
        if (!int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out int userId)) return;
        string value = contestId.ToString(CultureInfo.InvariantCulture);
        var now = clock.GetUtcNow();
        if (await db.Contests.AnyAsync(contest => contest.Id == contestId && now < contest.StartTime)
            && await db.UserClaims.AnyAsync(claim => claim.UserId == userId
                && claim.ClaimType == ClaimNames.ContestImport && claim.ClaimValue == value))
            context.Succeed(requirement);
    }
}

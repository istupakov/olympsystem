using System.Globalization;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;

using Olymp.Domain.Models;

namespace Olymp.WebApp.Services.Authorization;

public sealed class CancelRegistrationRequirement : IAuthorizationRequirement
{
}

internal sealed class CancelRegistrationHandler(TimeProvider timeProvider) : AuthorizationHandler<CancelRegistrationRequirement, Competitor>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
        CancelRegistrationRequirement requirement, Competitor participant)
    {
        var contest = participant.Contest;
        var now = timeProvider.GetUtcNow();
        if (contest.IsHidden || now >= contest.StartTime || now >= contest.EndTime
            || !int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out int userId))
            return Task.CompletedTask;

        bool allowed = contest.IsAcm
            ? participant.CoachId == userId && (!participant.IsApproved || participant.IsOutOfCompetition)
                && participant.OrganizationId is int organizationId
                && context.User.HasClaim(ClaimNames.CoachOrg, organizationId.ToString(CultureInfo.InvariantCulture))
            : participant.Members.Any(member => member.Id == userId);
        if (allowed)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

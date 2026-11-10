using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

using Olymp.Domain.Models;

namespace Olymp.WebApp.Components.Submissions;

public sealed class SubmitSolutionRequirement : IAuthorizationRequirement
{
    public bool ForJury { get; init; }
}

internal sealed class SubmitSolutionHandler(UserManager<User> users) : AuthorizationHandler<SubmitSolutionRequirement, Problem>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        SubmitSolutionRequirement requirement, Problem problem)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return;

        if (requirement.ForJury)
        {
            if (context.User.IsInRole(RoleNames.Jury) || context.User.IsInRole(RoleNames.Admin))
                context.Succeed(requirement);
            return;
        }

        var contest = problem.Contest;
        if (!problem.IsActive || !contest.AllowSubmitSolution)
            return;

        var user = await users.GetUserAsync(context.User);
        if (user is null || user.IsDisqualified)
            return;

        bool allowed = user switch
        {
            Competitor participant => participant.IsApproved && participant.ContestId == contest.Id
                && DateTimeOffset.Now <= contest.EndTime,
            OlympUser => contest.IsOpen,
            _ => false
        };
        if (allowed)
            context.Succeed(requirement);
    }
}

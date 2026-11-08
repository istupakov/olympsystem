using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

using Olymp.Domain.Models;

namespace Olymp.WebApp.Components.Submissions;

public sealed class SubmitSolutionRequirement : IAuthorizationRequirement
{
}

internal sealed class SubmitSolutionHandler(UserManager<User> users) : AuthorizationHandler<SubmitSolutionRequirement, Problem>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        SubmitSolutionRequirement requirement, Problem problem)
    {
        var contest = problem.Contest;
        if (!problem.IsActive || !contest.AllowSubmitSolution || context.User.Identity?.IsAuthenticated != true)
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

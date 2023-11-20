using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Services.Authorization;

public class ContestAuthorizationHandler(OlympContext context, UserManager<User> userManager) :
    AuthorizationHandler<OperationAuthorizationRequirement, Contest>
{
    private readonly OlympContext _context = context;
    private readonly UserManager<User> _userManager = userManager;

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
                                                         OperationAuthorizationRequirement requirement,
                                                         Contest contest)
    {
        if (context.User.IsInRole(RoleNames.Admin) || context.User.IsInRole(RoleNames.Jury))
        {
            context.Succeed(requirement);
            return;
        }

        var allow = requirement.Name switch
        {
            OperationNames.SubmitSolution => contest.AllowSubmitSolution,
            OperationNames.SendMessage => contest.AllowSendMessage,
            _ => false
        };

        var user = await _userManager.GetUserAsync(context.User);
        if (!allow || user is null || user.IsDisqualified)
            context.Fail();
        else if (user is Competitor { ContestId: int contestId })
        {
            if (contestId == contest.Id && DateTimeOffset.Now <= contest.EndTime)
                context.Succeed(requirement);
            else
                context.Fail();
        }
        else if (user is OlympUser olympUser)
        {
            if (contest.IsOpen)
                context.Succeed(requirement);
            else
            {
                var hasCompetitor = await _context.Competitors
                    .Where(x => x.ContestId == contest.Id && x.Members.Any(x => x.Id == user.Id))
                    .AnyAsync(x => x.IsApproved && x.IsOutOfCompetition && !x.IsDisqualified);

                if (hasCompetitor)
                    context.Succeed(requirement);
            }
        }
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Services.Authorization;

public class SubmissionAuthorizationHandler(OlympContext context, UserManager<User> userManager) :
    AuthorizationHandler<OperationAuthorizationRequirement, Submission>
{
    private readonly OlympContext _context = context;
    private readonly UserManager<User> _userManager = userManager;

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
                                                         OperationAuthorizationRequirement requirement,
                                                         Submission submission)
    {
        if (context.User.IsInRole(RoleNames.Admin) || context.User.IsInRole(RoleNames.Jury))
        {
            context.Succeed(requirement);
            return;
        }

        var userId = int.Parse(_userManager.GetUserId(context.User)!);
        if (userId == submission.UserId)
            context.Succeed(requirement);
        else
        {
            var isCompSubmission = await _context.Competitors
                .Where(x => x.Members.Any(x => x.Id == userId))
                .Select(x => x.Id)
                .ContainsAsync(submission.UserId);

            if (isCompSubmission)
                context.Succeed(requirement);
        }
    }
}

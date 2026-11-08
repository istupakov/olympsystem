using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.WebApp.Components.Submissions;

public sealed class ViewSolutionRequirement : IAuthorizationRequirement
{
}

internal sealed class ViewSolutionHandler(UserManager<User> users,
    IDbContextFactory<OlympContext> contextFactory,
    IHttpContextAccessor http) : AuthorizationHandler<ViewSolutionRequirement, Submission>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        ViewSolutionRequirement requirement, Submission submission)
    {
        if (!submission.Problem.IsActive || !submission.Problem.Contest.AllowShowProblems
            || context.User.Identity?.IsAuthenticated != true
            || !int.TryParse(users.GetUserId(context.User), out int userId))
            return;

        if (submission.UserId == userId)
        {
            context.Succeed(requirement);
            return;
        }

        if (!submission.Problem.Contest.IsOpen)
            return;

        var token = http.HttpContext?.RequestAborted ?? default;
        await using var db = await contextFactory.CreateDbContextAsync(token);
        bool isMemberOrCoach = await db.OlympUsers.AnyAsync(user => user.Id == userId
            && (user.Teams.Any(author => author.Id == submission.UserId)
                || user.Memberships.Any(team => team.Id == submission.UserId
                    && team.ContestId == submission.Problem.ContestId)), token);
        if (isMemberOrCoach)
            context.Succeed(requirement);
    }
}

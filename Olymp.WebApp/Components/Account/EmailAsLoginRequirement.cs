using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

using Olymp.Domain.Models;

namespace Olymp.WebApp.Components.Account;

internal sealed class EmailAsLoginRequirement : IAuthorizationRequirement
{
    public static bool NeedsMigration(User user) => user is OlympUser
        && (string.IsNullOrEmpty(user.NormalizedEmail) || user.NormalizedUserName != user.NormalizedEmail);
}

internal sealed class EmailAsLoginHandler(UserManager<User> users)
    : AuthorizationHandler<EmailAsLoginRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, EmailAsLoginRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return;

        var user = await users.GetUserAsync(context.User);
        if (user is not null && !EmailAsLoginRequirement.NeedsMigration(user))
            context.Succeed(requirement);
    }
}

internal sealed class EmailAsLoginRedirectHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Forbidden && result.AuthorizationFailure!.FailedRequirements.OfType<EmailAsLoginRequirement>().Any())
        {
            var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
            context.Response.Redirect(QueryHelpers.AddQueryString(
                context.Request.PathBase + "/Account/Manage/Email", "returnUrl", returnUrl));
            return Task.CompletedTask;
        }

        return _defaultHandler.HandleAsync(next, context, policy, result);
    }
}

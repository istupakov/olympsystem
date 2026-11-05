using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

using Olymp.Domain.Models;

namespace Olymp.WebApp.Components.Account;

internal static class IdentityComponentsEndpointRouteBuilderExtensions
{
    // These endpoints are required by the Identity Razor components defined in the /Components/Account/Pages directory of this project.
    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var accountGroup = endpoints.MapGroup("/Account");

        accountGroup.MapPost("/Logout", async (
            [FromServices] SignInManager<User> signInManager,
            [FromForm] string returnUrl) =>
        {
            await signInManager.SignOutAsync();
            return TypedResults.LocalRedirect($"~/{returnUrl}");
        });

        accountGroup.MapPost("/PasskeyCreationOptions", [RequireAntiforgeryToken] async (
            HttpContext context,
            [FromServices] UserManager<User> userManager,
            [FromServices] SignInManager<User> signInManager) =>
        {
            var antiforgeryValidationFeature = context.Features.Get<IAntiforgeryValidationFeature>();
            if (antiforgeryValidationFeature is not { IsValid: true })
            {
                return Results.BadRequest(antiforgeryValidationFeature?.Error?.Message ?? "Antiforgery validation failed.");
            }

            var user = await userManager.GetUserAsync(context.User);
            if (user is null)
            {
                return Results.NotFound($"Unable to load user with ID '{userManager.GetUserId(context.User)}'.");
            }

            var userId = await userManager.GetUserIdAsync(user);
            var userName = await userManager.GetUserNameAsync(user) ?? "User";
            var optionsJson = await signInManager.MakePasskeyCreationOptionsAsync(new()
            {
                Id = userId,
                Name = userName,
                DisplayName = userName
            });
            return TypedResults.Content(optionsJson, contentType: "application/json");
        }).RequireAuthorization(IdentityFeatures.PasskeysPolicy);

        accountGroup.MapPost("/PasskeyRequestOptions", [RequireAntiforgeryToken] async (
            HttpContext context,
            [FromServices] UserManager<User> userManager,
            [FromServices] SignInManager<User> signInManager,
            [FromQuery] string? username) =>
        {
            var antiforgeryValidationFeature = context.Features.Get<IAntiforgeryValidationFeature>();
            if (antiforgeryValidationFeature is not { IsValid: true })
            {
                return Results.BadRequest(antiforgeryValidationFeature?.Error?.Message ?? "Antiforgery validation failed.");
            }

            var user = string.IsNullOrEmpty(username) ? null : await userManager.FindByNameAsync(username);
            var optionsJson = await signInManager.MakePasskeyRequestOptionsAsync(user);
            return TypedResults.Content(optionsJson, contentType: "application/json");
        }).RequireAuthorization(IdentityFeatures.PasskeysPolicy);

        return accountGroup;
    }
}

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Olymp.WebApp.Components.Layout;

internal static class LanguageEndpoints
{
    public static IEndpointConventionBuilder MapLanguageEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/Language", ([FromForm] string culture, [FromForm] string returnUrl,
            HttpContext context, IOptions<RequestLocalizationOptions> localization) =>
        {
            var selected = localization.Value.SupportedUICultures?
                .FirstOrDefault(item => string.Equals(item.Name, culture, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
                return Results.BadRequest();

            context.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(selected)), new CookieOptions
                {
                    Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/",
                    HttpOnly = true,
                    Secure = context.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTimeOffset.UtcNow.AddYears(1)
                });

            return Results.LocalRedirect(RedirectHttpResult.IsLocalUrl(returnUrl) ? returnUrl : $"{context.Request.PathBase}/");
        }).AllowAnonymous();
}

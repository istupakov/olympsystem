using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Olymp.Site.Pages;

[AllowAnonymous]
public class LanguageModel(IOptions<RequestLocalizationOptions> localization) : PageModel
{
    public IActionResult OnGet() => LocalRedirect(Url.Content("~/"));

    public IActionResult OnPost(string? language, string? returnUrl)
    {
        if (!ModelState.IsValid)
            return BadRequest();

        var culture = localization.Value.SupportedUICultures?
            .FirstOrDefault(culture => string.Equals(culture.Name, language, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(language) && culture is null)
            return BadRequest();

        var cookieOptions = new CookieOptions
        {
            Path = Request.PathBase.HasValue ? Request.PathBase.Value : "/",
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddYears(1)
        };
        if (culture is null)
            Response.Cookies.Delete(CookieRequestCultureProvider.DefaultCookieName, cookieOptions);
        else
            Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)), cookieOptions);

        returnUrl = Url.IsLocalUrl(returnUrl) ? Url.Content(returnUrl!) : Url.Content("~/");
        // Query-string culture overrides the cookie; remove it when making an explicit choice.
        var queryIndex = returnUrl.IndexOf('?');
        if (queryIndex >= 0)
        {
            var query = QueryHelpers.ParseQuery(returnUrl[(queryIndex + 1)..]);
            query.Remove("culture");
            query.Remove("ui-culture");
            returnUrl = QueryHelpers.AddQueryString(returnUrl[..queryIndex], query);
        }

        return LocalRedirect(returnUrl);
    }
}

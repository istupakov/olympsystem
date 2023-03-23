// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#nullable disable

using System.ComponentModel.DataAnnotations;
using System.Globalization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Olymp.Site.IdentityUI.Areas.Identity.Pages.Account.Manage
{
    public class LanguageModel : PageModel
    {
        private readonly ILogger<LanguageModel> _logger;
        private readonly IOptions<RequestLocalizationOptions> _requestLocalizationOptions;

        public LanguageModel(
            ILogger<LanguageModel> logger,
            IOptions<RequestLocalizationOptions> requestLocalizationOptions)
        {
            _logger = logger;
            _requestLocalizationOptions = requestLocalizationOptions;
        }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [BindProperty]
        public InputModel Input { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [TempData]
        public string StatusMessage { get; set; }

        public SelectList Languages { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public class InputModel
        {
            /// <summary>
            ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
            ///     directly from your code. This API may change or be removed in future releases.
            /// </summary>
            [Display(Name = nameof(Language))]
            public string Language { get; set; }
        }

        public void Load()
        {
            Input = new InputModel { };
            Languages = new SelectList(_requestLocalizationOptions.Value.SupportedUICultures, nameof(CultureInfo.Name), nameof(CultureInfo.DisplayName));
            if (Request.Cookies.TryGetValue(CookieRequestCultureProvider.DefaultCookieName, out var value))
            {
                var culture = CookieRequestCultureProvider.ParseCookieValue(value);
                Input.Language = culture?.Cultures.FirstOrDefault().Value;
            }
        }

        public IActionResult OnGetAsync()
        {
            Load();
            return Page();
        }

        public IActionResult OnPostAsync()
        {
            if (!ModelState.IsValid)
            {
                Load();
                return Page();
            }

            if (string.IsNullOrEmpty(Input.Language))
            {
                Response.Cookies.Delete(CookieRequestCultureProvider.DefaultCookieName);
            }
            else
            {
                Response.Cookies.Append(
                    CookieRequestCultureProvider.DefaultCookieName,
                    CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(Input.Language)),
                    new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1) }
                );
            }

            _logger.LogInformation("User changed their language successfully.");
            StatusMessage = "Your language has been changed.";

            return RedirectToPage();
        }
    }
}

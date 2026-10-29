// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#nullable disable

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

using Olymp.Domain.Models;

namespace Olymp.Site.IdentityUI.Areas.Identity.Pages.Account
{
    [AllowAnonymous]
    public class RegisterConfirmationModel : PageModel
    {
        private readonly UserManager<User> _userManager;
        private readonly IStringLocalizer<IdentityUIResources> _localizer;

        public RegisterConfirmationModel(UserManager<User> userManager, IStringLocalizer<IdentityUIResources> localizer)
        {
            _userManager = userManager;
            _localizer = localizer;
        }

        public async Task<IActionResult> OnGetAsync(string email, string returnUrl = null)
        {
            if (email == null)
            {
                return RedirectToPage("/Index");
            }
            returnUrl = returnUrl ?? Url.Content("~/");

            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                return NotFound(_localizer["Unable to load user with email '{0}'.", email].Value);
            }

            return Page();
        }
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#nullable disable

using System.ComponentModel.DataAnnotations;
using System.Data;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.IdentityUI.Areas.Identity.Pages.Account.Manage
{
    public class IndexModel : PageModel
    {
        private readonly UserManager<User> _userManager;
        private readonly SignInManager<User> _signInManager;
        private readonly OlympContext _context;
        private readonly IStringLocalizer<IdentityUIResources> _localizer;

        public IndexModel(
            UserManager<User> userManager,
            SignInManager<User> signInManager,
            OlympContext context,
            IStringLocalizer<IdentityUIResources> localizer)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _context = context;
            _localizer = localizer;
        }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [Display(Name = nameof(Username))]
        public string Username { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [TempData]
        public string StatusMessage { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [BindProperty]
        public InputModel Input { get; set; }

        public SelectList Organizations { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public class InputModel
        {
            [StringLength(32, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")]
            [Required(ErrorMessage = "The {0} field is required.")]
            [RegularExpression(OlympUser.PublicNameRegex, ErrorMessage = "The field {0} must match the regular expression '{1}'.")]
            [Display(Name = "Public name")]
            public string PublicName { get; set; }

            [StringLength(64, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")]
            [RegularExpression(OlympUser.NameRegex, ErrorMessage = "The field {0} must match the regular expression '{1}'.")]
            [Display(Name = "First name")]
            public string FirstName { get; set; }

            [StringLength(64, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")]
            [RegularExpression(OlympUser.NameRegex, ErrorMessage = "The field {0} must match the regular expression '{1}'.")]
            [Display(Name = "Last name")]
            public string LastName { get; set; }

            [StringLength(64, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")]
            [RegularExpression(OlympUser.NameRegex, ErrorMessage = "The field {0} must match the regular expression '{1}'.")]
            [Display(Name = nameof(Patronymic))]
            public string Patronymic { get; set; }

            [Display(Name = "University")]
            public int? OrganizationId { get; set; }

            [StringLength(16, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")]
            [RegularExpression(OlympUser.GroupRegex, ErrorMessage = "The field {0} must match the regular expression '{1}'.")]
            [Display(Name = nameof(Group))]
            public string Group { get; set; }
        }

        private async Task LoadAsync(OlympUser user)
        {
            var userName = await _userManager.GetUserNameAsync(user);

            Username = userName;
            Organizations = new SelectList(await _context.Organizations.OrderBy(x => x.Name).ToListAsync(), nameof(Organization.Id), nameof(Organization.Name));

            Input = new InputModel
            {
                PublicName = user.Name,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Patronymic = user.Patronymic,
                OrganizationId = user.OrganizationId,
                Group = user.Details
            };
        }

        public async Task<IActionResult> OnGetAsync()
        {
            var user = await _userManager.GetUserAsync(User) as OlympUser;
            if (user is null)
            {
                return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
            }

            await LoadAsync(user);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var user = await _userManager.GetUserAsync(User) as OlympUser;
            if (user is null)
            {
                return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
            }

            if (Input.OrganizationId is not null && !(await _context.Organizations.AnyAsync(x => x.Id == Input.OrganizationId)))
                ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.OrganizationId)}", _localizer["Invalid value"]);

            if (!ModelState.IsValid)
            {
                await LoadAsync(user);
                return Page();
            }

            user.Name = Input.PublicName;
            user.FirstName = Input.FirstName;
            user.LastName = Input.LastName;
            user.Patronymic = Input.Patronymic;
            user.OrganizationId = Input.OrganizationId;
            user.Details = Input.Group;

            var updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                StatusMessage = "Error updating profile.";
                return RedirectToPage();
            }

            await _signInManager.RefreshSignInAsync(user);
            StatusMessage = "Your profile has been updated";
            return RedirectToPage();
        }
    }
}

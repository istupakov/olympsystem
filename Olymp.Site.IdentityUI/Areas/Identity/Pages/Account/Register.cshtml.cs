// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#nullable disable

using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.IdentityUI.Areas.Identity.Pages.Account
{
    public class RegisterModel : PageModel
    {
        private readonly SignInManager<User> _signInManager;
        private readonly UserManager<User> _userManager;
        private readonly IUserStore<User> _userStore;
        private readonly IUserEmailStore<User> _emailStore;
        private readonly ILogger<RegisterModel> _logger;
        private readonly IEmailSender _emailSender;
        private readonly OlympContext _context;
        private readonly IStringLocalizer<IdentityUIResources> _localizer;

        public RegisterModel(
            UserManager<User> userManager,
            IUserStore<User> userStore,
            SignInManager<User> signInManager,
            ILogger<RegisterModel> logger,
            IEmailSender emailSender,
            OlympContext context,
            IStringLocalizer<IdentityUIResources> localizer)
        {
            _userManager = userManager;
            _userStore = userStore;
            _emailStore = GetEmailStore();
            _signInManager = signInManager;
            _logger = logger;
            _emailSender = emailSender;
            _context = context;
            _localizer = localizer;
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
        public string ReturnUrl { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public IList<AuthenticationScheme> ExternalLogins { get; set; }

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

            /// <summary>
            ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
            ///     directly from your code. This API may change or be removed in future releases.
            /// </summary>
            [Required(ErrorMessage = "The {0} field is required.")]
            [EmailAddress(ErrorMessage = "The {0} field is not a valid e-mail address.")]
            [Display(Name = nameof(Email))]
            public string Email { get; set; }

            /// <summary>
            ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
            ///     directly from your code. This API may change or be removed in future releases.
            /// </summary>
            [Required(ErrorMessage = "The {0} field is required.")]
            [StringLength(100, ErrorMessage = "The {0} must be at least {2} and at max {1} characters long.", MinimumLength = 6)]
            [DataType(DataType.Password)]
            [Display(Name = nameof(Password))]
            public string Password { get; set; }

            /// <summary>
            ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
            ///     directly from your code. This API may change or be removed in future releases.
            /// </summary>
            [DataType(DataType.Password)]
            [Display(Name = "Confirm password")]
            [Compare("Password", ErrorMessage = "The password and confirmation password do not match.")]
            public string ConfirmPassword { get; set; }

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


        public async Task OnGetAsync(string returnUrl = null)
        {
            ReturnUrl = returnUrl;
            ExternalLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync()).ToList();
            Organizations = new SelectList(await _context.Organizations.OrderBy(x => x.Name).ToListAsync(), nameof(Organization.Id), nameof(Organization.Name));
        }

        public async Task<IActionResult> OnPostAsync(string returnUrl = null)
        {
            returnUrl ??= Url.Content("~/");
            ExternalLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync()).ToList();

            if (Input.OrganizationId is not null && !(await _context.Organizations.AnyAsync(x => x.Id == Input.OrganizationId)))
                ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.OrganizationId)}", _localizer["Invalid value"]);

            if (ModelState.IsValid)
            {
                var user = CreateUser();
                user.Name = Input.PublicName;
                user.FirstName = Input.FirstName;
                user.LastName = Input.LastName;
                user.Patronymic = Input.Patronymic;
                user.OrganizationId = Input.OrganizationId;
                user.Details = Input.Group;
                user.RegistrationDate = DateTimeOffset.Now;

                await _userStore.SetUserNameAsync(user, Input.Email, CancellationToken.None);
                await _emailStore.SetEmailAsync(user, Input.Email, CancellationToken.None);
                var result = await _userManager.CreateAsync(user, Input.Password);

                if (result.Succeeded)
                {
                    _logger.LogInformation("User created a new account with password.");

                    var userId = await _userManager.GetUserIdAsync(user);
                    var code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
                    code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
                    var callbackUrl = Url.Page(
                        "/Account/ConfirmEmail",
                        pageHandler: null,
                        values: new { area = "Identity", userId, code, returnUrl },
                        protocol: Request.Scheme);

                    await _emailSender.SendEmailAsync(Input.Email, _localizer["Confirm your email"],
                        string.Format(_localizer["Please confirm your account by <a href='{0}'>clicking here</a>."],
                        HtmlEncoder.Default.Encode(callbackUrl)));

                    if (_userManager.Options.SignIn.RequireConfirmedAccount)
                    {
                        return RedirectToPage("RegisterConfirmation", new { email = Input.Email, returnUrl });
                    }
                    else
                    {
                        await _signInManager.SignInAsync(user, isPersistent: false);
                        return LocalRedirect(returnUrl);
                    }
                }
                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }
            }

            ReturnUrl = returnUrl;
            Organizations = new SelectList(await _context.Organizations.OrderBy(x => x.Name).ToListAsync(), nameof(Organization.Id), nameof(Organization.Name));

            // If we got this far, something failed, redisplay form
            return Page();
        }

        private OlympUser CreateUser()
        {
            try
            {
                return Activator.CreateInstance<OlympUser>();
            }
            catch
            {
                throw new InvalidOperationException($"Can't create an instance of '{nameof(OlympUser)}'. " +
                    $"Ensure that '{nameof(OlympUser)}' is not an abstract class and has a parameterless constructor, or alternatively " +
                    $"override the register page in /Areas/Identity/Pages/Account/Register.cshtml");
            }
        }

        private IUserEmailStore<User> GetEmailStore()
        {
            if (!_userManager.SupportsUserEmail)
            {
                throw new NotSupportedException("The default UI requires a user store with email support.");
            }
            return (IUserEmailStore<User>)_userStore;
        }
    }
}

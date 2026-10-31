using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

using Olymp.Site.Services;
using Olymp.Site.Services.AntiCheat;

namespace Olymp.Site.Pages.AntiCheat;

public class ByUserModel(AntiCheatReportService reports, IStringLocalizer<SharedResource> localizer) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "User name")]
        public string? UserName { get; set; }

        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Similarity threshold")]
        [Range(0, 1, ErrorMessage = "Similarity threshold must be in range [0, 1]")]
        public float? Threshold { get; set; }
    }

    public IEnumerable<PlagiarismReport> Reports { get; private set; } = [];

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPost(CancellationToken token)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            Reports = await reports.FindPlagiarismByUserAsync(Input.UserName!, Input.Threshold!.Value, token);
        }
        catch (EntityNotFoundException)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.UserName)}", localizer["User not found."]);
        }

        return Page();
    }
}

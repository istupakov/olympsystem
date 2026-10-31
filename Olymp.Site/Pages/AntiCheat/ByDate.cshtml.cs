using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

using Olymp.Site.Services.AntiCheat;

namespace Olymp.Site.Pages.AntiCheat;

public class ByDateModel(AntiCheatReportService reports, IStringLocalizer<SharedResource> localizer) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Date from")]
        public DateTime? StartDate { get; set; }

        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Date to")]
        public DateTime? StopDate { get; set; }

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
        if (Input.StartDate > Input.StopDate)
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.StopDate)}",
                localizer["The end date must be on or after the start date."]);

        if (!ModelState.IsValid)
        {
            return Page();
        }

        Reports = await reports.FindPlagiarismByDateAsync(Input.StartDate!.Value, Input.StopDate!.Value,
            Input.Threshold!.Value, token);

        return Page();
    }
}

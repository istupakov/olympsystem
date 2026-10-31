using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Olymp.Checker;
using Olymp.Domain;

namespace Olymp.Site.Pages.AntiCheat;

public class AIDetectionTestModel(OlympContext context, IAIDetectionService aiDetectionService, IStringLocalizer<SharedResource> localizer) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Submission 1")]
        public int? SubmissionId { get; set; }
    }

    public AIDetectionResult? Report { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPost()
    {
        var submission = await context.Submissions
            .Include(x => x.Compilator)
            .SingleOrDefaultAsync(x => x.Id == Input.SubmissionId);

        if (submission is null)
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.SubmissionId)}", localizer["Invalid value"]);

        if (ModelState.IsValid)
        {
            Report = aiDetectionService.DetectAI(submission!.Text, submission!.Compilator.Language);
        }

        return Page();
    }
}

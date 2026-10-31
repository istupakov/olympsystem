using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Olymp.Checker;
using Olymp.Domain;

namespace Olymp.Site.Pages.AntiCheat;

public class PlagiarismTestModel(OlympContext context, ISubmissionSimilarityService submissionSimilarityService, IStringLocalizer<SharedResource> localizer) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Submission 1")]
        public int? SubmissionId1 { get; set; }

        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Submission 2")]
        public int? SubmissionId2 { get; set; }
    }

    public float? Similarity { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPost()
    {
        var submission1 = await context.Submissions.SingleOrDefaultAsync(x => x.Id == Input.SubmissionId1);
        if (submission1 is null)
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.SubmissionId1)}", localizer["Invalid value"]);

        var submission2 = await context.Submissions.SingleOrDefaultAsync(x => x.Id == Input.SubmissionId2);
        if (submission2 is null)
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.SubmissionId2)}", localizer["Invalid value"]);

        if (ModelState.IsValid)
        {
            Similarity = submissionSimilarityService.CompareSimilarity(submission1!.Text, submission2!.Text);
        }

        return Page();
    }
}

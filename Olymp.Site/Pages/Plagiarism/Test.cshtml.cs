using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Olymp.Domain;
using Olymp.Site.Services.Plagiarism;

namespace Olymp.Site.Pages.Plagiarism;

public class TestModel : PageModel
{
    private readonly OlympContext _context;
    private readonly ISubmissionSimilarityService _submissionSimilarityService;
    private readonly IStringLocalizer<SharedResource> _localizer;

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

    public TestModel(OlympContext context, ISubmissionSimilarityService submissionSimilarityService, IStringLocalizer<SharedResource> localizer)
    {
        _context = context;
        _submissionSimilarityService = submissionSimilarityService;
        _localizer = localizer;
    }

    public void OnGet()
    {

    }

    public async Task<IActionResult> OnPost()
    {
        var submission1 = await _context.Submissions.SingleOrDefaultAsync(x => x.Id == Input.SubmissionId1);
        if (submission1 is null)
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.SubmissionId1)}", _localizer["Invalid value"]);

        var submission2 = await _context.Submissions.SingleOrDefaultAsync(x => x.Id == Input.SubmissionId2);
        if (submission2 is null)
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.SubmissionId2)}", _localizer["Invalid value"]);

        if (ModelState.IsValid)
        {
            Similarity = _submissionSimilarityService.Similarity(submission1!, submission2!);
        }

        return Page();
    }
}

using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Olymp.Domain;
using Olymp.Site.Services.AntiCheat;

namespace Olymp.Site.Pages.AntiCheat;

public class AIDetectionTestModel(OlympContext context, IAIDetectionServiceService aiDetectionServiceService, IStringLocalizer<SharedResource> localizer) : PageModel
{
    private readonly OlympContext _context = context;
    private readonly IAIDetectionServiceService _aiDetectionServiceService = aiDetectionServiceService;
    private readonly IStringLocalizer<SharedResource> _localizer = localizer;

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
        var submission = await _context.Submissions
            .Include(x => x.Compilator)
            .SingleOrDefaultAsync(x => x.Id == Input.SubmissionId);

        if (submission is null)
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.SubmissionId)}", _localizer["Invalid value"]);

        if (ModelState.IsValid)
        {
            Report = _aiDetectionServiceService.DetectAI(submission!);
        }

        return Page();
    }
}

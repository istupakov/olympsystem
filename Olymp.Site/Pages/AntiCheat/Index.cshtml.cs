using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site.Services.AntiCheat;
using Olymp.Site.Services.Authorization;

namespace Olymp.Site.Pages.AntiCheat;

public class IndexModel(OlympContext context, AntiCheatReportService reports,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public SelectList ContestList { get; private set; } = null!;

    public class InputModel
    {
        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Contest")]
        public int? ContestId { get; set; }

        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Similarity threshold")]
        [Range(0, 1, ErrorMessage = "Similarity threshold must be in range [0, 1]")]
        public float? Threshold { get; set; }
    }

    public IEnumerable<PlagiarismReport> PlagiarismReports { get; private set; } = [];
    public IEnumerable<AIUsingReport> AIUsingReports { get; private set; } = [];

    public async Task LoadAsync()
    {
        var contests = await context.Contests.OrderBy(x => x.StartTime)
            .Select(x => new { x.Id, x.Name }).ToListAsync();
        ContestList = new SelectList(contests.Where(x => ContestAccess.CanManage(User, x.Id)),
            nameof(Contest.Id), nameof(Contest.Name));
    }

    public async Task<IActionResult> OnGet()
    {
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPost(string action, CancellationToken token)
    {
        await LoadAsync();

        if (Input.ContestId is int contestId && !ContestAccess.CanManage(User, contestId))
            return Forbid();

        if (!ModelState.IsValid)
            return Page();

        if (!await context.Contests.AnyAsync(x => x.Id == Input.ContestId, token))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.ContestId)}", localizer["Invalid value"]);
            return Page();
        }

        if (action == "AIUsing")
            AIUsingReports = await reports.DetectContestAiAsync(Input.ContestId!.Value, Input.Threshold!.Value, token);
        else
            PlagiarismReports = await reports.FindContestPlagiarismAsync(Input.ContestId!.Value, Input.Threshold!.Value, token);

        return Page();
    }
}

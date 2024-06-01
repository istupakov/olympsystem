using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site.Services.Plagiarism;

namespace Olymp.Site.Pages.Plagiarism;

public record PlagiarismCase(Submission Submission1, Submission Submission2, double Similarity);
public record PlagiarismReport(User Competitor1, User Competitor2, IEnumerable<PlagiarismCase> Cases);

public class IndexModel(OlympContext context, ISubmissionSimilarityService submissionSimilarityService,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    private readonly OlympContext _context = context;
    private readonly ISubmissionSimilarityService _submissionSimilarityService = submissionSimilarityService;
    private readonly IStringLocalizer<SharedResource> _localizer = localizer;

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

    public IEnumerable<PlagiarismReport> Reports { get; private set; } = [];

    public async Task LoadAsync()
    {
        var contests = await _context.Contests.OrderBy(x => x.StartTime).ToListAsync();
        ContestList = new SelectList(contests, nameof(Contest.Id), nameof(Contest.Name));
    }

    public async Task<IActionResult> OnGet()
    {
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPost(CancellationToken token)
    {
        await LoadAsync();

        var contest = await _context.Contests.AsSplitQuery()
                                    .Include(x => x.Competitors)
                                    .ThenInclude(x => x.Submissions)
                                    .ThenInclude(x => x.Problem)
                                    .Include(x => x.Competitors)
                                    .ThenInclude(x => x.Submissions)
                                    .ThenInclude(x => x.Compilator)
                                    .SingleOrDefaultAsync(x => x.Id == Input.ContestId, cancellationToken: token);
        if (contest is null)
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.ContestId)}", _localizer["Invalid value"]);

        if (!ModelState.IsValid)
        {
            return Page();
        }

        Reports = from user1 in contest!.Competitors
                  from user2 in contest.Competitors
                  where user1.Id < user2.Id
                  let cases = from sol1 in user1.Submissions
                              from sol2 in user2.Submissions
                              where sol1.ProblemId == sol2.ProblemId
                              let similarity = _submissionSimilarityService.Similarity(sol1, sol2)
                              where similarity > Input.Threshold
                              select new PlagiarismCase(sol1, sol2, similarity)
                  where cases.Any()
                  select new PlagiarismReport(user1, user2, cases);

        return Page();
    }
}

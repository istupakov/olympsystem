using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Site.Services.Plagiarism;

namespace Olymp.Site.Pages.Plagiarism;

public class ByDateModel(OlympContext context, ISubmissionSimilarityService submissionSimilarityService) : PageModel
{
    private readonly OlympContext _context = context;
    private readonly ISubmissionSimilarityService _submissionSimilarityService = submissionSimilarityService;

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

    public async Task<IActionResult> OnPost()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var users = await _context.Users.AsSplitQuery()
                                    .Include(x => x.Submissions)
                                    .ThenInclude(x => x.Problem)
                                    .Include(x => x.Submissions)
                                    .ThenInclude(x => x.Compilator)
                                    .ToListAsync();

        Reports = from user1 in users
                  from user2 in users
                  where user1.Id < user2.Id
                  let cases = from sol1 in user1.Submissions
                              where sol1.CommitTime > Input.StartDate!.Value
                                  && sol1.CommitTime < Input.StopDate!.Value
                              from sol2 in user2.Submissions
                              where sol1.ProblemId == sol2.ProblemId
                              let similarity = _submissionSimilarityService.Similarity(sol1, sol2)
                              where similarity > Input.Threshold!.Value
                              select new PlagiarismCase(sol1, sol2, similarity)
                  where cases.Any()
                  select new PlagiarismReport(user1, user2, cases);

        return Page();
    }
}

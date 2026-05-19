using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Site.Services.AntiCheat;

namespace Olymp.Site.Pages.AntiCheat;

public class ByUserModel(OlympContext context, ISubmissionSimilarityService submissionSimilarityService) : PageModel
{
    private readonly OlympContext _context = context;
    private readonly ISubmissionSimilarityService _submissionSimilarityService = submissionSimilarityService;

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
        var user1=users.FirstOrDefault(u=>u.UserName == Input.UserName || u.Name == Input.UserName);
        if (user1 == null) return Page();

        Reports = from user2 in users                  
                  let cases = from sol1 in user1.Submissions
                              from sol2 in user2.Submissions
                              where sol1.ProblemId == sol2.ProblemId
                              let similarity = _submissionSimilarityService.CompareSimilarity(sol1, sol2)
                              where similarity > Input.Threshold!.Value
                              select new PlagiarismCase(sol1, sol2, similarity)
                  where cases.Any()
                  select new PlagiarismReport(user1, user2, cases);

        return Page();
    }
}

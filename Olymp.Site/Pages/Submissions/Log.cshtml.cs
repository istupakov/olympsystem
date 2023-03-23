using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Pages.Submissions;

public class LogModel : PageModel
{
    private readonly OlympContext _context;

    public IEnumerable<CheckResult> CheckResults { get; private set; } = null!;

    public LogModel(OlympContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        CheckResults = await _context.CheckResults
            .Where(x => x.SubmissionId == id)
            .OrderBy(x => x.CheckTime)
            .Include(x => x.Submission)
            .ThenInclude(x => x.User)
            .Include(x => x.Submission)
            .ThenInclude(x => x.Compilator)
            .Include(x => x.Submission)
            .ThenInclude(x => x.Problem)
            .ThenInclude(x => x.Contest)
            .AsSplitQuery()
            .ToListAsync();

        return Page();
    }
}

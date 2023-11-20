using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Pages.Problems;


public class ShowTextModel(OlympContext context) : PageModel
{
    private readonly OlympContext _context = context;

    public Problem Problem { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var problem = await _context.Problems
            .Include(x => x.Contest)
            .Include(x => x.Tests.Where(x => x.IsOpen && x.IsActive).OrderBy(x => x.Number))
            .SingleOrDefaultAsync(x => x.Id == id);

        if (problem is null)
            return NotFound();

        Problem = problem;

        if (problem.Contest.AllowDownloadProblems && problem.IsActive || User.IsInRole(RoleNames.Admin) || User.IsInRole(RoleNames.Jury))
            return Page();
        return Forbid();
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Pages.Submissions;

public class IndexModel(OlympContext context) : PageModel
{
    private readonly OlympContext _context = context;

    public IEnumerable<Submission> Submissions { get; private set; } = null!;

    public int N { get; private set; }

    public async Task<IActionResult> OnGetAsync(int n = 1, string? user = null, string? problem = null, string? compilator = null)
    {
        N = n;
        if (N < 1)
            return NotFound();

        if (!User.IsInRole(RoleNames.Admin) && !User.IsInRole(RoleNames.Jury))
        {
            if (N >= 10 || user is not null || problem is not null || compilator is not null)
                return Forbid();
        }

        var query = _context.Submissions
            .Where(x => user == null || x.User.Name.Contains(user))
            .Where(x => problem == null || x.Problem.Name.Contains(problem))
            .Where(x => compilator == null || x.Compilator.Name.Contains(compilator))
            .OrderByDescending(x => x.CommitTime)
            .Include(x => x.User)
            .ThenInclude(x => x.Organization)
            .Include(x => x.Problem)
            .ThenInclude(x => x.Contest)
            .Include(x => x.Compilator)
            .AsQueryable();

        if (!User.IsInRole(RoleNames.Admin) && !User.IsInRole(RoleNames.Jury))
        {
            query = query.Where(x => x.User is OlympUser && !x.User.IsHidden && !x.Problem.Contest.IsHidden);
        }

        const int pageSize = 10;
        Submissions = await query
            .Skip((N - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync();

        if (!Submissions.Any())
            return NotFound();
        return Page();
    }
}

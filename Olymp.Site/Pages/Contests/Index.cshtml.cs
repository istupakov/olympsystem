using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Pages.Contests;

public class IndexModel(OlympContext context) : PageModel
{
    private readonly OlympContext _context = context;

    public IEnumerable<Contest> Contests { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync()
    {
        Contests = await _context.Contests
            .Where(x => !x.IsHidden || User.IsInRole(RoleNames.Admin) || User.IsInRole(RoleNames.Jury))
            .OrderByDescending(x => x.StartTime)
            .ToListAsync();

        return Page();
    }
}

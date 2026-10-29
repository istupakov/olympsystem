using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site.Services.Authorization;

namespace Olymp.Site.Pages.Contests.Competitors;

public class IndexModel(OlympContext context) : PageModel
{
    private readonly OlympContext _context = context;

    public Contest Contest { get; private set; } = null!;
    public IEnumerable<Competitor>? Competitors { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var canManage = ContestAccess.CanManage(User, id);
        var contest = await _context.Contests
            .Where(x => !x.IsHidden || canManage)
            .Include(x => x.Competitors)
            .ThenInclude(x => x.Organization)
            .Include(x => x.Competitors)
            .ThenInclude(x => x.Coach)
            .Include(x => x.Competitors)
            .ThenInclude(x => x.Members)
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == id);

        if (contest is null)
            return NotFound();

        Contest = contest;
        Competitors = Contest.Competitors.Where(x => !x.IsHidden).OrderBy(x => x.Name);
        ViewData["IsManage"] = false;

        return Page();
    }
}

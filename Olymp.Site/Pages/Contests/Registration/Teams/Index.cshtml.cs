using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Pages.Contests.Registration.Teams;

public class IndexModel : PageModel
{
    private readonly OlympContext _context;
    private readonly UserManager<User> _userManager;

    public Contest Contest { get; private set; } = null!;
    public IEnumerable<Organization> Organizations { get; private set; } = null!;
    public IEnumerable<Competitor> Teams { get; private set; } = null!;
    public int UserId { get; private set; }

    public IndexModel(OlympContext context, UserManager<User> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var contest = await _context.Contests
            .SingleOrDefaultAsync(x => x.Id == id);

        if (contest is null)
            return NotFound();

        if (DateTimeOffset.Now > contest.EndTime)
            return RedirectToPage("/Contests/Competitors/Index", new { id });

        Contest = contest;

        UserId = int.Parse(_userManager.GetUserId(User)!);
        var orgIds = User.Claims.Where(x => x.Type == ClaimNames.CoachOrg).Select(x => int.Parse(x.Value)).ToList();
        Organizations = await _context.Organizations
            .Where(x => orgIds.Contains(x.Id))
            .ToListAsync();
        Teams = await _context.Competitors
            .Where(x => x.ContestId == id)
            .Where(x => orgIds.Contains(x.OrganizationId!.Value))
            .Include(x => x.Coach)
            .Include(x => x.Organization)
            .Include(x => x.Members)
            .OrderBy(x => x.RegistrationDate)
            .AsSplitQuery()
            .ToListAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, int teamId)
    {
        var contest = await _context.Contests
            .SingleOrDefaultAsync(x => x.Id == id);

        var team = await _context.Competitors
            .Include(x => x.Members)
            .AsTracking()
            .SingleOrDefaultAsync(x => x.ContestId == id && x.Id == teamId);

        if (contest is null || team is null)
            return NotFound();

        var coachId = int.Parse(_userManager.GetUserId(User)!);
        var orgIds = User.Claims.Where(x => x.Type == ClaimNames.CoachOrg).Select(x => int.Parse(x.Value)).ToList();
        if (DateTimeOffset.Now >= contest.StartTime || team.IsApproved || team.CoachId != coachId ||
            !orgIds.Contains(team.OrganizationId!.Value))
            return Forbid();

        team.Members.Clear();
        await _userManager.DeleteAsync(team);

        return RedirectToPage();
    }
}

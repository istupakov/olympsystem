using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Pages.Contests.Registration.Teams;

public class IndexModel(OlympContext context, UserManager<User> userManager, ILogger<IndexModel> logger,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    private readonly OlympContext _context = context;
    private readonly UserManager<User> _userManager = userManager;

    public Contest Contest { get; private set; } = null!;
    public IEnumerable<Organization> Organizations { get; private set; } = null!;
    public IEnumerable<Competitor> Teams { get; private set; } = null!;
    public int UserId { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var contest = await _context.Contests
            .SingleOrDefaultAsync(x => x.Id == id && !x.IsHidden);

        if (contest is null || !contest.IsAcm)
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
            .SingleOrDefaultAsync(x => x.Id == id && !x.IsHidden);

        var team = await _context.Competitors
            .Include(x => x.Members)
            .AsTracking()
            .SingleOrDefaultAsync(x => x.ContestId == id && x.Id == teamId);

        if (contest is null || !contest.IsAcm || team is null)
            return NotFound();

        var coachId = int.Parse(_userManager.GetUserId(User)!);
        var orgIds = User.Claims.Where(x => x.Type == ClaimNames.CoachOrg).Select(x => int.Parse(x.Value)).ToList();
        if (DateTimeOffset.Now >= contest.StartTime || team.IsApproved || team.CoachId != coachId ||
            !orgIds.Contains(team.OrganizationId!.Value))
            return Forbid();

        if (await _context.Submissions.AnyAsync(x => x.UserId == team.Id)
            || await _context.Messages.AnyAsync(x => x.UserId == team.Id))
            return BadRequest(localizer["The participant cannot be deleted because submissions or other related records exist."].Value);

        team.Members.Clear();
        try
        {
            var result = await _userManager.DeleteAsync(team);
            if (!result.Succeeded)
                return BadRequest(result.Errors);
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Failed to delete participant {CompetitorId}", team.Id);
            return BadRequest(localizer["Could not delete the participant. Please contact the administrator."].Value);
        }

        return RedirectToPage();
    }
}

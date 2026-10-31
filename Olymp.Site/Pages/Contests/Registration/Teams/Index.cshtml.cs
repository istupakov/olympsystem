using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site.Services;
using Olymp.Site.Services.Contests;

namespace Olymp.Site.Pages.Contests.Registration.Teams;

public class IndexModel(OlympContext context, UserManager<User> userManager,
    CompetitorManagementService competitors) : PageModel
{
    public Contest Contest { get; private set; } = null!;
    public IEnumerable<Organization> Organizations { get; private set; } = null!;
    public IEnumerable<Competitor> Teams { get; private set; } = null!;
    public int UserId { get; private set; }

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext handlerContext, PageHandlerExecutionDelegate next)
    {
        var executed = await next();
        IActionResult? result = executed.Exception switch
        {
            EntityNotFoundException => NotFound(),
            IdentityOperationException identityError => BadRequest(identityError.Errors),
            OperationValidationException error => BadRequest(error.Message),
            _ => null
        };
        if (result is not null)
        {
            executed.Result = result;
            executed.ExceptionHandled = true;
        }
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var contest = await context.Contests
            .SingleOrDefaultAsync(x => x.Id == id && !x.IsHidden);

        if (contest is null || !contest.IsAcm)
            return NotFound();

        if (DateTimeOffset.Now > contest.EndTime)
            return RedirectToPage("/Contests/Competitors/Index", new { id });

        Contest = contest;

        UserId = int.Parse(userManager.GetUserId(User)!);
        var orgIds = User.Claims.Where(x => x.Type == ClaimNames.CoachOrg).Select(x => int.Parse(x.Value)).ToList();
        Organizations = await context.Organizations
            .Where(x => orgIds.Contains(x.Id))
            .ToListAsync();
        Teams = await context.Competitors
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
        var contest = await context.Contests
            .SingleOrDefaultAsync(x => x.Id == id && !x.IsHidden);

        var team = await context.Competitors
            .SingleOrDefaultAsync(x => x.ContestId == id && x.Id == teamId);

        if (contest is null || !contest.IsAcm || team is null)
            return NotFound();

        var coachId = int.Parse(userManager.GetUserId(User)!);
        var orgIds = User.Claims.Where(x => x.Type == ClaimNames.CoachOrg).Select(x => int.Parse(x.Value)).ToList();
        if (DateTimeOffset.Now >= contest.StartTime || team.IsApproved || team.CoachId != coachId ||
            !orgIds.Contains(team.OrganizationId!.Value))
            return Forbid();

        await competitors.DeleteAsync(id, team.Id, HttpContext.RequestAborted);

        return RedirectToPage();
    }
}

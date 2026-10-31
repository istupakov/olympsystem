using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site.Services;
using Olymp.Site.Services.Contests;

namespace Olymp.Site.Pages.Contests.Registration;

public class IndexModel(OlympContext context, UserManager<User> userManager,
    CompetitorManagementService competitors,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    public Contest Contest { get; private set; } = null!;

    [Display(Name = "Competitor's name")]
    public string CompetitorName { get; private set; } = null!;

    public bool HasGroup { get; private set; }
    public bool AlreadyRegistered { get; private set; }

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

    private async Task<Competitor?> LoadUserAsync()
    {
        var userId = int.Parse(userManager.GetUserId(User)!);
        var user = await context.OlympUsers
            .Include(x => x.Organization)
            .Include(x => x.Memberships.Where(x => x.ContestId == Contest.Id))
            .SingleOrDefaultAsync(x => x.Id == userId, HttpContext.RequestAborted) ?? throw new EntityNotFoundException();
        var competitor = user.Memberships.SingleOrDefault();

        if (competitor is not null)
        {
            AlreadyRegistered = true;
            CompetitorName = competitor.Name!;
            return competitor;
        }

        CompetitorName = CompetitorManagementService.GetParticipantName(user);
        HasGroup = user.Details is not null;

        var template = localizer["The {0} field in the profile must be filled in."];
        if (user.FirstName is null)
            ModelState.AddModelError(string.Empty, string.Format(template, localizer["First name"]));
        if (user.LastName is null)
            ModelState.AddModelError(string.Empty, string.Format(template, localizer["Last name"]));
        if (user.Organization?.Name is null)
            ModelState.AddModelError(string.Empty, string.Format(template, localizer["University"]));

        if (!ModelState.IsValid)
            ModelState.AddModelError(nameof(CompetitorName), localizer["Registration information is incorrect."]);

        return null;
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var contest = await context.Contests
            .SingleOrDefaultAsync(x => x.Id == id && !x.IsHidden);

        if (contest is null)
            return NotFound();

        Contest = contest;

        if (DateTimeOffset.Now > contest.EndTime)
            return RedirectToPage("/Contests/Competitors/Index", new { id });

        if (Contest.IsAcm && User.HasClaim(x => x.Type == ClaimNames.CoachOrg))
            return RedirectToPage("./Teams/Index", new { id });

        await LoadUserAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(int id, bool ooc)
    {
        var contest = await context.Contests
            .SingleOrDefaultAsync(x => x.Id == id && !x.IsHidden);

        if (contest is null)
            return NotFound();

        if (contest.IsAcm)
            return Forbid();

        Contest = contest;

        await LoadUserAsync();

        if (DateTimeOffset.Now > contest.EndTime || AlreadyRegistered || (!HasGroup && !ooc))
            return Forbid();

        if (!ModelState.IsValid)
            return Page();

        var userId = int.Parse(userManager.GetUserId(User)!);
        await competitors.RegisterIndividualAsync(id, userId, ooc, HttpContext.RequestAborted);

        return RedirectToPage("/Contests/Competitors/Index", new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var contest = await context.Contests
            .SingleOrDefaultAsync(x => x.Id == id && !x.IsHidden);

        if (contest is null)
            return NotFound();

        if (contest.IsAcm)
            return Forbid();

        Contest = contest;

        var competitor = await LoadUserAsync();

        if (DateTimeOffset.Now >= contest.StartTime || competitor is null)
            return Forbid();

        await competitors.DeleteAsync(id, competitor.Id, HttpContext.RequestAborted);

        return RedirectToPage("/Contests/Competitors/Index", new { id });
    }
}

using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Pages.Contests.Registration;

public class IndexModel : PageModel
{
    private readonly OlympContext _context;
    private readonly UserManager<User> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public Contest Contest { get; private set; } = null!;

    [Display(Name = "Competitor's name")]
    public string CompetitorName { get; private set; } = null!;

    public bool HasGroup { get; private set; }
    public bool AlreadyRegistered { get; private set; }

    public IndexModel(OlympContext context, UserManager<User> userManager,
        IStringLocalizer<SharedResource> localizer)
    {
        _context = context;
        _userManager = userManager;
        _localizer = localizer;
    }

    private async Task<OlympUser> LoadUser()
    {
        var user = (OlympUser)(await _userManager.GetUserAsync(User))!;
        _context.Attach(user);
        await _context.Entry(user).Reference(x => x.Organization).LoadAsync();

        var competitor = await _context.Entry(user)
            .Collection(x => x.Memberships)
            .Query()
            .SingleOrDefaultAsync(x => x.ContestId == Contest.Id);

        if (competitor is not null)
        {
            AlreadyRegistered = true;
            CompetitorName = competitor.Name!;
            return user;
        }

        CompetitorName = $"{user.FirstName} {user.LastName}, {user.Organization?.Name}";
        if (user.Details is not null)
        {
            HasGroup = true;
            CompetitorName += $", {user.Details}";
        }

        var template = _localizer["The {0} field in the profile must be filled in."];
        if (user.FirstName is null)
            ModelState.AddModelError(string.Empty, string.Format(template, _localizer["First name"]));
        if (user.LastName is null)
            ModelState.AddModelError(string.Empty, string.Format(template, _localizer["Last name"]));
        if (user.Organization?.Name is null)
            ModelState.AddModelError(string.Empty, string.Format(template, _localizer["University"]));

        if (!ModelState.IsValid)
            ModelState.AddModelError(nameof(CompetitorName), _localizer["Registration information is incorrect."]);

        return user;
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var contest = await _context.Contests
            .SingleOrDefaultAsync(x => x.Id == id);

        if (contest is null)
            return NotFound();

        Contest = contest;

        if (DateTimeOffset.Now > contest.EndTime)
            return RedirectToPage("/Contests/Competitors/Index", new { id });

        if (Contest.IsAcm && User.HasClaim(x => x.Type == ClaimNames.CoachOrg))
            return RedirectToPage("./Teams/Index", new { id });

        await LoadUser();

        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(int id, bool ooc)
    {
        var contest = await _context.Contests
            .SingleOrDefaultAsync(x => x.Id == id);

        if (contest is null)
            return NotFound();

        Contest = contest;

        var user = await LoadUser();

        if (DateTimeOffset.Now > contest.EndTime || AlreadyRegistered || !HasGroup && !ooc)
            return Forbid();

        if (!ModelState.IsValid)
            return Page();

        var competitor = new Competitor
        {
            Name = CompetitorName,
            Contest = null!,
            ContestId = Contest.Id,
            IsHidden = user.IsHidden,
            IsOutOfCompetition = ooc,
            OrganizationId = user.OrganizationId,
            RegistrationDate = DateTimeOffset.Now,
            SecurityStamp = Guid.NewGuid().ToString(),
            Members = new List<OlympUser>(),
            Messages = Array.Empty<Message>(),
            Submissions = Array.Empty<Submission>()
        };
        competitor.Members.Add(user);
        await _userManager.CreateAsync(competitor);

        return RedirectToPage("/Contests/Competitors/Index", new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var contest = await _context.Contests
            .SingleOrDefaultAsync(x => x.Id == id);

        if (contest is null)
            return NotFound();

        Contest = contest;

        var user = await LoadUser();

        if (DateTimeOffset.Now >= contest.StartTime || !AlreadyRegistered)
            return Forbid();

        var competitor = await _context.Entry(user)
            .Collection(x => x.Memberships)
            .Query()
            .Include(x => x.Members)
            .AsTracking()
            .SingleOrDefaultAsync(x => x.ContestId == Contest.Id);

        if (competitor is null)
            return Forbid();

        competitor.Members.Clear();
        await _userManager.DeleteAsync(competitor);

        return RedirectToPage("/Contests/Competitors/Index", new { id });
    }
}

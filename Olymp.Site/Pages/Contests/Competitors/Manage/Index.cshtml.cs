using System.Security.Claims;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Pages.Contests.Competitors.Manage;

public class IndexModel : PageModel
{
    private readonly OlympContext _context;
    private readonly UserManager<User> _userManager;

    public Contest Contest { get; private set; } = null!;
    public IEnumerable<Competitor>? Competitors { get; private set; } = null!;

    [BindProperty]
    public IFormFile? LoginsFile { get; set; }

    public IndexModel(OlympContext context, UserManager<User> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var contest = await _context.Contests
            .Where(x => !x.IsHidden || User.IsInRole(RoleNames.Admin))
            .Include(x => x.Competitors.OrderBy(x => x.RegistrationDate))
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
        Competitors = Contest.Competitors.Where(x => !x.IsHidden);
        ViewData["IsManage"] = true;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int userId, string action)
    {
        var competitor = await _context.Competitors
            .AsTracking()
            .SingleOrDefaultAsync(x => x.Id == userId);

        if (competitor is null)
            return NotFound();

        switch (action)
        {
            case "Manage":
                return RedirectToPage("/Users/Manage", new { id = userId });
            case "Approve":
                competitor.IsApproved = true;
                await _userManager.UpdateAsync(competitor);
                break;
            case "Disapprove":
                competitor.IsApproved = false;
                await _userManager.UpdateAsync(competitor);
                break;
            case "InCompetition":
                competitor.IsOutOfCompetition = false;
                await _userManager.UpdateAsync(competitor);
                break;
            case "OutOfCompetition":
                competitor.IsOutOfCompetition = true;
                await _userManager.UpdateAsync(competitor);
                break;
            case "Disqualify":
                competitor.IsDisqualified = true;
                await _userManager.UpdateAsync(competitor);
                break;
            case "CancelDisqualification":
                competitor.IsDisqualified = false;
                await _userManager.UpdateAsync(competitor);
                break;
            case "Delete":
                await _context.Entry(competitor).Collection(x => x.Members).LoadAsync();
                competitor.Members.Clear();
                await _userManager.DeleteAsync(competitor);
                break;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostApproveAllAsync(int id)
    {
        var contest = await _context.Contests
            .Include(x => x.Competitors)
            .AsTracking()
            .SingleOrDefaultAsync(x => x.Id == id);

        if (contest is null)
            return NotFound();

        foreach (var competitor in contest.Competitors)
        {
            competitor.IsApproved = true;
            await _userManager.UpdateAsync(competitor);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUploadLoginsAsync(int id)
    {
        var contest = await _context.Contests
            .Include(x => x.Competitors.OrderBy(x => x.RegistrationDate))
            .ThenInclude(x => x.Members)
            .Include(x => x.Competitors)
            .ThenInclude(x => x.Coach)
            .AsTracking()
            .SingleOrDefaultAsync(x => x.Id == id);

        if (contest is null)
            return NotFound();

        if (LoginsFile is null)
            return BadRequest();

        var logins = new Dictionary<string, string>();
        using (var reader = new StreamReader(LoginsFile.OpenReadStream()))
        {
            while ((await reader.ReadLineAsync())?.Split(';', '\t', ',') is [var login, var password])
                logins.Add(login, password);
        }

        foreach (var competitor in contest.Competitors)
        {
            var username = await _userManager.GetUserNameAsync(competitor);
            if (username is null)
            {
                var (login, password) = logins.FirstOrDefault();
                if (login is null)
                    return BadRequest("Too few logins in file");

                logins.Remove(login);

                var res = await _userManager.SetUserNameAsync(competitor, login);
                if (!res.Succeeded)
                    return BadRequest(res.Errors);

                competitor.PasswordHash = password;
                res = await _userManager.UpdateAsync(competitor);
                if (!res.Succeeded)
                    return BadRequest(res.Errors);

                res = await _userManager.AddClaimAsync(competitor, new Claim(ClaimNames.CompetitorContest, id.ToString()));
                if (!res.Succeeded)
                    return BadRequest(res.Errors);
            }
            else
            {
                logins.Remove(username);
            }
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveLoginsAsync(int id)
    {
        var contest = await _context.Contests
            .Include(x => x.Competitors)
            .AsTracking()
            .SingleOrDefaultAsync(x => x.Id == id);

        if (contest is null)
            return NotFound();

        foreach (var competitor in contest.Competitors)
        {
            var res = await _userManager.SetUserNameAsync(competitor, null);
            if (!res.Succeeded)
                return BadRequest(res.Errors);

            res = await _userManager.RemovePasswordAsync(competitor);
            if (!res.Succeeded)
                return BadRequest(res.Errors);

            res = await _userManager.RemoveClaimAsync(competitor, new Claim(ClaimNames.CompetitorContest, id.ToString()));
            if (!res.Succeeded)
                return BadRequest(res.Errors);
        }

        return RedirectToPage();
    }
}

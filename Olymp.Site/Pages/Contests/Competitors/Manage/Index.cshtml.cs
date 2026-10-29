using System.Data;
using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site.Services.Authorization;

namespace Olymp.Site.Pages.Contests.Competitors.Manage;

[Authorize(Policy = PolicyNames.JuryOrAdmin)]
public class IndexModel(OlympContext context, UserManager<User> userManager,
    ILogger<IndexModel> logger,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    private readonly OlympContext _context = context;
    private readonly UserManager<User> _userManager = userManager;

    public Contest Contest { get; private set; } = null!;
    public IEnumerable<Competitor>? Competitors { get; private set; } = null!;

    [BindProperty]
    public IFormFile? LoginsFile { get; set; }

    [BindProperty]
    public IFormFile? ParticipantsFile { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (!context.HandlerArguments.TryGetValue("id", out var value) || value is not int id
            || !ContestAccess.CanManage(User, id))
        {
            context.Result = Forbid();
            return;
        }
        await next();
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var contest = await _context.Contests
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

    public async Task<IActionResult> OnPostAsync(int id, int userId, string action)
    {
        var competitor = await _context.Competitors
            .AsTracking()
            .SingleOrDefaultAsync(x => x.Id == userId && x.ContestId == id);

        if (competitor is null)
            return NotFound();

        IdentityResult result;
        switch (action)
        {
            case "Manage":
                if (!User.IsInRole(RoleNames.Admin))
                    return Forbid();
                return RedirectToPage("/Users/Manage", new { id = userId });
            case "Approve":
                competitor.IsApproved = true;
                result = await _userManager.UpdateAsync(competitor);
                break;
            case "Disapprove":
                competitor.IsApproved = false;
                result = await _userManager.UpdateAsync(competitor);
                break;
            case "InCompetition":
                competitor.IsOutOfCompetition = false;
                result = await _userManager.UpdateAsync(competitor);
                break;
            case "OutOfCompetition":
                competitor.IsOutOfCompetition = true;
                result = await _userManager.UpdateAsync(competitor);
                break;
            case "Disqualify":
                competitor.IsDisqualified = true;
                result = await _userManager.UpdateAsync(competitor);
                break;
            case "CancelDisqualification":
                competitor.IsDisqualified = false;
                result = await _userManager.UpdateAsync(competitor);
                break;
            case "Delete":
                if (await _context.Submissions.AnyAsync(x => x.UserId == competitor.Id)
                    || await _context.Messages.AnyAsync(x => x.UserId == competitor.Id))
                    return BadRequest(localizer["The participant cannot be deleted because submissions or other related records exist."].Value);
                await _context.Entry(competitor).Collection(x => x.Members).LoadAsync();
                competitor.Members.Clear();
                try
                {
                    result = await _userManager.DeleteAsync(competitor);
                }
                catch (DbUpdateException ex)
                {
                    logger.LogError(ex, "Failed to delete participant {CompetitorId}", competitor.Id);
                    return BadRequest(localizer["Could not delete the participant. Please contact the administrator."].Value);
                }
                break;
            default:
                return BadRequest();
        }

        if (!result.Succeeded)
            return BadRequest(result.Errors);
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
            var result = await _userManager.UpdateAsync(competitor);
            if (!result.Succeeded)
                return BadRequest(result.Errors);
        }

        return RedirectToPage();
    }

    private async Task<IActionResult> CreateLoginsAsync(int id, Dictionary<string, string> logins)
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

    private async Task<IActionResult> CreateParticipantsAsync(int id, List<string> names)
    {
        if (names.Count == 0)
            return BadRequest(localizer["The file contains no participant names."].Value);
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            return BadRequest(localizer["The file contains duplicate participant names."].Value);

        await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var contest = await _context.Contests
            .Include(x => x.Competitors)
            .SingleOrDefaultAsync(x => x.Id == id);

        if (contest is null)
            return NotFound();
        if (contest.IsAcm)
            return BadRequest(localizer["Participant names can only be imported into individual contests."].Value);

        var existing = contest.Competitors.ToLookup(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var name in names)
        {
            var count = existing[name].Count();
            if (count > 1)
                return BadRequest(localizer["The contest contains multiple participants named '{0}'.", name].Value);
            if (count == 1)
                continue;

            var competitor = new Competitor
            {
                ContestId = id,
                Contest = null!,
                Name = name,
                IsApproved = true,
                RegistrationDate = DateTimeOffset.Now,
                SecurityStamp = Guid.NewGuid().ToString(),
                Members = [],
                Messages = [],
                Submissions = []
            };
            var res = await _userManager.CreateAsync(competitor);
            if (!res.Succeeded)
                return BadRequest(res.Errors);
            added++;
        }

        await transaction.CommitAsync();
        StatusMessage = localizer["Added participants: {0}; already existed: {1}.", added, names.Count - added];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUploadParticipantsAsync(int id)
    {
        if (ParticipantsFile is null || ParticipantsFile.Length == 0)
            return BadRequest();

        var names = new List<string>();
        using (var reader = new StreamReader(ParticipantsFile.OpenReadStream()))
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var name = line.Trim();
                if (name.Length > 0)
                    names.Add(name);
            }
        }

        return await CreateParticipantsAsync(id, names);
    }

    public async Task<IActionResult> OnPostUploadLoginsAsync(int id)
    {
        if (LoginsFile is null)
            return BadRequest();

        var logins = new Dictionary<string, string>();
        using (var reader = new StreamReader(LoginsFile.OpenReadStream()))
        {
            while ((await reader.ReadLineAsync())?.Split(';', '\t', ',') is [var login, var password])
                logins.Add(login.Trim(), password.Trim());
        }

        return await CreateLoginsAsync(id, logins);
    }

    public async Task<IActionResult> OnPostCreateRandomLoginsAsync(int id)
    {
        int count = await _context.Competitors.CountAsync(x => x.ContestId == id);

        const int passwordLength = 10;
        var passwordSymbols = new List<char>();
        passwordSymbols.AddRange(Enumerable.Range(0, 10).Select(x => (char)('0' + x)));
        passwordSymbols.AddRange(Enumerable.Range(0, 26).Select(x => (char)('a' + x)));
        passwordSymbols.AddRange(Enumerable.Range(0, 26).Select(x => (char)('A' + x)));

        var random = new Random();
        var logins = new Dictionary<string, string>();
        for (int i = 1; i <= count; ++i)
        {
            var password = new StringBuilder();
            for (int j = 0; j < passwordLength; ++j)
                password.Append(passwordSymbols[random.Next(passwordSymbols.Count)]);
            logins.Add($"olymp{i:000}", password.ToString());
        }

        return await CreateLoginsAsync(id, logins);
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

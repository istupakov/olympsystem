using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site.Services;
using Olymp.Site.Services.Authorization;
using Olymp.Site.Services.Contests;

namespace Olymp.Site.Pages.Contests.Competitors.Manage;

[Authorize(Policy = PolicyNames.JuryOrAdmin)]
public class IndexModel(OlympContext context,
    CompetitorManagementService participants,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    private readonly OlympContext _context = context;

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
        Competitors = Contest.Competitors;
        ViewData["IsManage"] = true;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, int userId, string action)
    {
        if (action == "Manage")
        {
            if (!User.IsInRole(RoleNames.Admin))
                return Forbid();
            if (!await _context.Competitors.AnyAsync(x => x.Id == userId && x.ContestId == id, HttpContext.RequestAborted))
                return NotFound();
            return RedirectToPage("/Users/Manage", new { id = userId });
        }
        var token = HttpContext.RequestAborted;
        var operation = action switch
        {
            "Approve" => participants.SetApprovalAsync(id, userId, true, token),
            "Disapprove" => participants.SetApprovalAsync(id, userId, false, token),
            "InCompetition" => participants.SetOutOfCompetitionAsync(id, userId, false, token),
            "OutOfCompetition" => participants.SetOutOfCompetitionAsync(id, userId, true, token),
            "Disqualify" => participants.SetDisqualificationAsync(id, userId, true, token),
            "CancelDisqualification" => participants.SetDisqualificationAsync(id, userId, false, token),
            "Delete" => participants.DeleteAsync(id, userId, token),
            _ => null
        };
        if (operation is null)
            return BadRequest();
        await operation;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostApproveAllAsync(int id)
    {
        await participants.ApproveAllAsync(id, HttpContext.RequestAborted);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteWithoutSubmissionsAsync(int id)
    {
        var count = await participants.DeleteWithoutSubmissionsAsync(id, HttpContext.RequestAborted);
        StatusMessage = localizer["Deleted participants: {0}.", count];
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

        var summary = await participants.ImportAsync(id, names, HttpContext.RequestAborted);
        StatusMessage = localizer["Added participants: {0}; already existed: {1}.", summary.Added, summary.Existing];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUploadLoginsAsync(int id)
    {
        if (LoginsFile is null)
            return BadRequest();

        var logins = new Dictionary<string, string>();
        using (var reader = new StreamReader(LoginsFile.OpenReadStream()))
        {
            while ((await reader.ReadLineAsync())?.Split(';', '\t', ',') is [var login, var password])
                if (!logins.TryAdd(login.Trim(), password.Trim()))
                    return BadRequest(localizer["The file contains duplicate logins."].Value);
        }

        await participants.AssignLoginsAsync(id, logins, HttpContext.RequestAborted);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateRandomLoginsAsync(int id)
    {
        await participants.GenerateLoginsAsync(id, HttpContext.RequestAborted);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveLoginsAsync(int id)
    {
        await participants.RemoveLoginsAsync(id, HttpContext.RequestAborted);
        return RedirectToPage();
    }
}

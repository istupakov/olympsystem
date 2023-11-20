using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Pages.Contests.Submits;

public class SendMessageModel(OlympContext context, UserManager<User> userManager,
    IAuthorizationService authorizationService, IStringLocalizer<SharedResource> localizer) : PageModel
{
    private readonly OlympContext _context = context;
    private readonly UserManager<User> _userManager = userManager;
    private readonly IAuthorizationService _authorizationService = authorizationService;
    private readonly IStringLocalizer<SharedResource> _localizer = localizer;

    public Contest Contest { get; private set; } = null!;
    public SelectList Problems { get; private set; } = null!;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Display(Name = "Problem")]
        public int? ProblemId { get; set; }

        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Question")]
        [StringLength(4 * 1024, MinimumLength = 10, ErrorMessage = "The {0} must be at least {2} and at max {1} characters long.")]
        public string Text { get; set; } = null!;
    }

    private async Task<bool> LoadAsync(int id)
    {
        var contest = await _context.Contests
            .Include(x => x.Problems.Where(x => x.IsActive).OrderBy(x => x.Number))
            .SingleOrDefaultAsync(x => x.Id == id);

        if (contest is null)
            return false;

        Contest = contest;
        Problems = new SelectList(Contest.Problems, nameof(Problem.Id), nameof(Problem.NameWithNumber));

        return true;
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var load = await LoadAsync(id);
        if (!load)
            return NotFound();

        var res = await _authorizationService.AuthorizeAsync(User, Contest,
            new OperationAuthorizationRequirement { Name = OperationNames.SendMessage });

        return res.Succeeded ? Page() : Forbid();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var load = await LoadAsync(id);
        if (!load)
            return NotFound();

        var res = await _authorizationService.AuthorizeAsync(User, Contest,
            new OperationAuthorizationRequirement { Name = OperationNames.SendMessage });

        if (!res.Succeeded)
            return Forbid();

        if (Input.ProblemId is not null && !Contest.Problems.Any(x => x.Id == Input.ProblemId))
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.ProblemId)}", _localizer["Invalid value"]);

        if (!ModelState.IsValid)
            return Page();

        var userId = int.Parse(_userManager.GetUserId(User)!);
        var competitorId = await _context.Competitors
            .Where(x => x.ContestId == id && x.Members.Any(x => x.Id == userId))
            .Select(x => (int?)x.Id)
            .SingleOrDefaultAsync();

        var message = new Message
        {
            User = null!,
            UserId = Contest.IsOpen ? userId : competitorId ?? userId,
            Problem = null!,
            ProblemId = Input.ProblemId,
            Contest = null!,
            ContestId = Contest.Id,
            UserText = Input.Text,
            SendTime = DateTimeOffset.Now
        };

        _context.Messages.Add(message);
        await _context.SaveChangesAsync();

        return RedirectToPage("./Index", null, new { id }, "messages-tab-pane");
    }
}

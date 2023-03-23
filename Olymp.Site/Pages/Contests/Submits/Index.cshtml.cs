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

public class IndexModel : PageModel
{
    private readonly OlympContext _context;
    private readonly UserManager<User> _userManager;
    private readonly IAuthorizationService _authorizationService;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public Contest Contest { get; private set; } = null!;
    public SelectList CompilatorList { get; private set; } = null!;
    public SelectList ProblemList { get; private set; } = null!;
    public IEnumerable<Compilator> Compilators { get; private set; } = null!;
    public IEnumerable<Submission> Submissions { get; private set; } = null!;
    public IEnumerable<Domain.Standing.UserProblemResult> UserStanding { get; private set; } = null!;
    public int CommonMessageCount { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Problem")]
        public int? ProblemId { get; set; }

        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Compilator")]
        public int? CompilatorId { get; set; }

        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Source code")]
        [StringLength(32 * 1024, MinimumLength = 10, ErrorMessage = "The {0} must be at least {2} and at max {1} characters long.")]
        public string Code { get; set; } = null!;
    }

    public IndexModel(OlympContext context, UserManager<User> userManager,
        IAuthorizationService authorizationService, IStringLocalizer<SharedResource> localizer)
    {
        _context = context;
        _userManager = userManager;
        _authorizationService = authorizationService;
        _localizer = localizer;
    }

    private async Task<bool> LoadAsync(int id)
    {
        var userId = int.Parse(_userManager.GetUserId(User)!);
        var competitorId = await _context.Competitors
            .Where(x => x.ContestId == id && x.Members.Any(x => x.Id == userId))
            .Select(x => x.Id)
            .SingleOrDefaultAsync();

        var contest = await _context.Contests
            .Include(x => x.Problems.Where(x => x.IsActive).OrderBy(x => x.Number))
            .ThenInclude(x => x.Submissions.Where(x => x.UserId == userId || x.UserId == competitorId)
                .OrderByDescending(x => x.CommitTime))
            .ThenInclude(x => x.Compilator)
            .Include(x => x.Messages.Where(x => x.Problem == null || x.Problem.IsActive)
                .Where(x => x.UserId == null || x.UserId == userId || x.UserId == competitorId)
                .OrderByDescending(x => x.SendTime))
            .ThenInclude(x => x.Problem)
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == id);

        if (contest is null)
            return false;

        Compilators = await _context.Compilators
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .ToListAsync();

        Contest = contest;
        CompilatorList = new SelectList(Compilators, nameof(Compilator.Id), nameof(Compilator.NameWithDescription));
        ProblemList = new SelectList(Contest.Problems, nameof(Problem.Id), nameof(Problem.NameWithNumber));
        Submissions = Contest.Problems.SelectMany(x => x.Submissions).OrderByDescending(x => x.CommitTime).ToList();
        UserStanding = Domain.Standing.UserProblemStanding(Contest.Problems, Submissions);
        CommonMessageCount = Contest.Messages.Count(message => message.UserId is null);
        ViewData["IsSchool"] = contest.IsSchool;
        Input.CompilatorId ??= Submissions.Select(x => x.CompilatorId).FirstOrDefault();

        return true;
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var load = await LoadAsync(id);
        if (!load)
            return NotFound();

        var res = await _authorizationService.AuthorizeAsync(User, Contest,
            new OperationAuthorizationRequirement { Name = OperationNames.SubmitSolution });

        return res.Succeeded ? Page() : Forbid();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var load = await LoadAsync(id);
        if (!load)
            return NotFound();

        var res = await _authorizationService.AuthorizeAsync(User, Contest,
            new OperationAuthorizationRequirement { Name = OperationNames.SubmitSolution });

        if (!res.Succeeded)
            return Forbid();

        if (!Compilators.Any(x => x.Id == Input.CompilatorId))
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.CompilatorId)}", _localizer["Invalid value"]);

        if (!Contest.Problems.Any(x => x.Id == Input.ProblemId))
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.ProblemId)}", _localizer["Invalid value"]);

        if (!ModelState.IsValid)
            return Page();

        var userId = int.Parse(_userManager.GetUserId(User)!);
        var competitorId = await _context.Competitors
            .Where(x => x.ContestId == id && x.Members.Any(x => x.Id == userId))
            .Select(x => (int?)x.Id)
            .SingleOrDefaultAsync();

        var submission = new Submission
        {
            User = null!,
            UserId = Contest.IsOpen ? userId : competitorId ?? userId,
            Problem = null!,
            ProblemId = Input.ProblemId!.Value,
            Compilator = null!,
            CompilatorId = Input.CompilatorId!.Value,
            SourceCode = null!,
            Text = Input.Code!,
            CommitTime = DateTimeOffset.Now,
            LastModification = DateTimeOffset.Now,
            CheckResults = Array.Empty<CheckResult>()
        };

        _context.Submissions.Add(submission);
        await _context.SaveChangesAsync();

        return RedirectToPage(null, null, "submissions-tab-pane");
    }
}

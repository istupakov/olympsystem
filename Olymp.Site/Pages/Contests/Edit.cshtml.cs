using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site.Services.Authorization;
using Olymp.Site.Services.Contests;

namespace Olymp.Site.Pages.Contests;

[Authorize(Policy = PolicyNames.JuryOrAdmin)]
[RequestSizeLimit(128 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 128 * 1024 * 1024)]
public class EditModel(OlympContext context, ContestProblemService problems,
    IOptions<ContestImportOptions> options, ILogger<EditModel> logger,
    UserManager<User> users, SignInManager<User> signIn, IStringLocalizer<SharedResource> localizer) : PageModel
{
    public Contest Contest { get; private set; } = new()
    {
        Name = "",
        Abbr = "",
        Problems = [],
        Messages = [],
        Competitors = []
    };
    public bool IsNew { get; private set; } = true;
    public IReadOnlyList<ProblemSummary> Problems { get; private set; } = [];
    public record ProblemSummary(int Id, int Number, string Name, bool IsActive, int TestCount, int OpenTestCount);

    public InputModel Input { get; set; } = new();
    [BindProperty]
    public IFormFile? ProblemsFile { get; set; }
    [BindProperty]
    public IFormFile? PdfFile { get; set; }
    [TempData]
    public string? StatusMessage { get; set; }

    private async Task<bool> LoadAsync(int id)
    {
        var contest = await context.Contests.AsTracking().SingleOrDefaultAsync(x => x.Id == id, HttpContext.RequestAborted);
        if (contest is null)
            return false;
        Contest = contest;
        IsNew = false;
        Problems = await context.Problems.Where(x => x.ContestId == id).OrderBy(x => x.Number)
            .Select(x => new ProblemSummary(x.Id, x.Number, x.Name, x.IsActive, x.Tests.Count, x.Tests.Count(t => t.IsOpen)))
            .ToListAsync(HttpContext.RequestAborted);
        return true;
    }

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null)
            return Page();
        if (!ContestAccess.CanManage(User, id.Value))
            return Forbid();
        if (!await LoadAsync(id.Value))
            return NotFound();
        Input = InputModel.From(Contest);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id, InputModel input, CancellationToken token)
    {
        Input = input;
        if (id is not null)
        {
            if (!ContestAccess.CanManage(User, id.Value))
                return Forbid();
            if (!await LoadAsync(id.Value))
                return NotFound();
        }
        if (await context.Contests.AnyAsync(x => x.Id != id && x.Abbr == Input.Abbr, token))
            ModelState.AddModelError("Input.Abbr", localizer["This abbreviation is already in use."]);
        if (!ModelState.IsValid)
            return Page();
        Input.ApplyTo(Contest);
        if (IsNew)
        {
            var user = await users.GetUserAsync(User);
            if (user is null)
                return Forbid();
            await using (var transaction = await context.Database.BeginTransactionAsync(token))
            {
                context.Contests.Add(Contest);
                await context.SaveChangesAsync(token);
                var result = await users.AddClaimAsync(user,
                    new Claim(ClaimNames.ContestManage, Contest.Id.ToString(CultureInfo.InvariantCulture)));
                if (!result.Succeeded)
                {
                    foreach (var error in result.Errors)
                        ModelState.AddModelError(string.Empty, error.Description);
                    return Page();
                }
                await transaction.CommitAsync(token);
            }
            await signIn.RefreshSignInAsync(user);
        }
        else
            await context.SaveChangesAsync(token);
        StatusMessage = localizer["Settings saved."];
        return RedirectToPage(new { id = Contest.Id });
    }

    public Task<IActionResult> OnPostUnfreezeAsync(int id) => RunAsync(id, async () =>
    {
        await context.Contests.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.FreezeTime, (DateTimeOffset?)null), HttpContext.RequestAborted);
        return localizer["Standings unfrozen."];
    });

    public Task<IActionResult> OnPostUploadPdfAsync(int id) => RunAsync(id, async () =>
    {
        if (PdfFile is null)
            throw new InvalidDataException(localizer["Select a statements PDF."]);
        if (PdfFile.Length <= 0 || PdfFile.Length > options.Value.MaxPdfBytes)
            throw new InvalidDataException(localizer["Invalid file size. Maximum: {0} MiB.", options.Value.MaxPdfBytes / 1024 / 1024]);

        using var target = new MemoryStream();
        await PdfFile.CopyToAsync(target, HttpContext.RequestAborted);
        await problems.UploadPdfAsync(id, target.ToArray(), HttpContext.RequestAborted);
        return localizer["Statements PDF uploaded."];
    });

    public Task<IActionResult> OnPostRemovePdfAsync(int id) => RunAsync(id, async () =>
    {
        await problems.RemovePdfAsync(id, HttpContext.RequestAborted);
        return localizer["Statements PDF removed."];
    });

    public Task<IActionResult> OnPostGenerateTestsAsync(int id) => RunAsync(id, async () =>
    {
        await problems.GenerateTestZipAsync(id, HttpContext.RequestAborted);
        return localizer["Tests ZIP generated."];
    });

    public Task<IActionResult> OnPostImportProblemsAsync(int id) => RunAsync(id, async () =>
    {
        if (ProblemsFile is null)
            throw new InvalidDataException(localizer["Select a problems ZIP."]);
        if (ProblemsFile.Length <= 0 || ProblemsFile.Length > options.Value.MaxZipBytes)
            throw new InvalidDataException(localizer["Invalid file size. Maximum: {0} MiB.", options.Value.MaxZipBytes / 1024 / 1024]);
        using var source = ProblemsFile.OpenReadStream();
        var count = await problems.ImportAsync(id, source, HttpContext.RequestAborted);
        return localizer["Imported problems: {0}. After changing tests, retest submissions and regenerate the tests ZIP.", count];
    });

    public Task<IActionResult> OnPostRetestAllAsync(int id, int problemId) => RetestAsync(id, problemId, false);
    public Task<IActionResult> OnPostRetestExceptAcceptedAsync(int id, int problemId) => RetestAsync(id, problemId, true);

    public Task<IActionResult> OnPostRetestCompilationErrorsAsync(int id) => RunAsync(id, async () =>
    {
        var count = await problems.RetestCompilationErrorsAsync(id, HttpContext.RequestAborted);
        return localizer["Submissions queued for retesting: {0}.", count];
    });

    private Task<IActionResult> RetestAsync(int id, int problemId, bool exceptAccepted) => RunAsync(id, async () =>
    {
        var count = await problems.RetestAsync(id, problemId, exceptAccepted, HttpContext.RequestAborted);
        return localizer["Submissions queued for retesting: {0}.", count];
    });

    private async Task<IActionResult> RunAsync(int id, Func<Task<string>> action)
    {
        if (!ContestAccess.CanManage(User, id))
            return Forbid();
        if (!await context.Contests.AnyAsync(x => x.Id == id, HttpContext.RequestAborted))
            return NotFound();
        try
        {
            StatusMessage = await action();
            return RedirectToPage(new { id });
        }
        catch (InvalidDataException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Failed to update contest {ContestId}", id);
            ModelState.AddModelError(string.Empty, localizer["Could not save the changes. Refresh the page and try again."]);
        }
        context.ChangeTracker.Clear();
        await LoadAsync(id);
        Input = InputModel.From(Contest);
        return Page();
    }

    public sealed class InputModel : IValidatableObject
    {
        [Required(ErrorMessage = "The {0} field is required."), StringLength(256, ErrorMessage = "The field {0} must be a string with a maximum length of {1}."), Display(Name = "Name")]
        public string Name { get; set; } = "";

        [Required(ErrorMessage = "The {0} field is required."), StringLength(128, ErrorMessage = "The field {0} must be a string with a maximum length of {1}."), RegularExpression("[A-Za-z][A-Za-z0-9_-]*", ErrorMessage = "The field {0} must match the regular expression '{1}'."), Display(Name = "Abbreviation")]
        public string Abbr { get; set; } = "";

        [Display(Name = "Description")]
        public string? Description { get; set; }

        [Required(ErrorMessage = "The {0} field is required."), Display(Name = "Start time")]
        public DateTime? StartTime { get; set; } = DateTime.Now.Date.AddDays(1).AddHours(10);

        [Required(ErrorMessage = "The {0} field is required."), Display(Name = "End time")]
        public DateTime? EndTime { get; set; } = DateTime.Now.Date.AddDays(1).AddHours(15);

        [Display(Name = "Problem show time")]
        public DateTime? ProblemShowTime { get; set; }

        [Display(Name = "Freeze time")]
        public DateTime? FreezeTime { get; set; }

        [Display(Name = "Hidden contest")]
        public bool IsHidden { get; set; } = true;

        [Display(Name = "Open participation")]
        public bool IsOpen { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            var localizer = validationContext.GetRequiredService<IStringLocalizer<SharedResource>>();
            if (StartTime >= EndTime)
                yield return new(localizer["Start time must be before end time."], [nameof(EndTime)]);
            if (FreezeTime is not null && (FreezeTime < StartTime || FreezeTime > EndTime))
                yield return new(localizer["Freeze time must be within the contest."], [nameof(FreezeTime)]);
            foreach (var (field, time) in new[] { (nameof(StartTime), StartTime), (nameof(EndTime), EndTime),
                         (nameof(ProblemShowTime), ProblemShowTime), (nameof(FreezeTime), FreezeTime) })
                if (time is not null && (TimeZoneInfo.Local.IsInvalidTime(time.Value) || TimeZoneInfo.Local.IsAmbiguousTime(time.Value)))
                    yield return new(localizer["Specify an unambiguous time in the server time zone."], [field]);
        }

        public void ApplyTo(Contest contest)
        {
            contest.Name = Name.Trim();
            contest.Abbr = Abbr.Trim();
            contest.Description = Description;
            contest.StartTime = ToOffset(StartTime)!.Value;
            contest.EndTime = ToOffset(EndTime)!.Value;
            contest.ProblemShowTime = ToOffset(ProblemShowTime);
            contest.FreezeTime = ToOffset(FreezeTime);
            contest.IsHidden = IsHidden;
            contest.IsOpen = IsOpen;
        }

        public static InputModel From(Contest contest) => new()
        {
            Name = contest.Name,
            Abbr = contest.Abbr,
            Description = contest.Description,
            StartTime = contest.StartTime.LocalDateTime,
            EndTime = contest.EndTime.LocalDateTime,
            ProblemShowTime = contest.ProblemShowTime?.LocalDateTime,
            FreezeTime = contest.FreezeTime?.LocalDateTime,
            IsHidden = contest.IsHidden,
            IsOpen = contest.IsOpen
        };

        private static DateTimeOffset? ToOffset(DateTime? time) => time is null ? null
            : new DateTimeOffset(DateTime.SpecifyKind(time.Value, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(time.Value));
    }
}

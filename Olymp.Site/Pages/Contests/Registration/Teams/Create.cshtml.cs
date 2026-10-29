using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Data.Common;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Pages.Contests.Registration.Teams;

public class CreateModel(OlympContext context, UserManager<User> userManager,
    IStringLocalizer<SharedResource> localizer, ILogger<CreateModel> logger) : PageModel
{
    private readonly OlympContext _context = context;
    private readonly UserManager<User> _userManager = userManager;
    private readonly IStringLocalizer<SharedResource> _localizer = localizer;

    public Contest? Contest { get; private set; }
    public Organization? Organization { get; private set; }

    public bool NeedConfirmation { get; private set; }

    [Display(Name = "Team name")]
    public string? TeamName { get; private set; }

    [Display(Name = "Team members")]
    public string? TeamMembers { get; private set; }

    public string? TeamInfo { get; private set; }
    public OlympUser? Member1 { get; private set; }
    public OlympUser? Member2 { get; private set; }
    public OlympUser? Member3 { get; private set; }

    public IEnumerable<OlympUser>? Participants { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        private const string TeamReqex = @"[A-Za-z]+([- ][A-Za-z]+)*";
        private const string ParticipantReqex = @"[А-ЯЁа-яёA-Za-z0-9- ]+";

        [Required(ErrorMessage = "The {0} field is required.")]
        [StringLength(32, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")]
        [RegularExpression(TeamReqex, ErrorMessage = "The {0} field must contain Latin letters, with words separated by a single space or hyphen.")]
        [Display(Name = "University (team prefix)")]
        public string? TeamPrefix { get; set; } = null!;

        [Display(Name = "Team number")]
        [Range(1, 32, ErrorMessage = "The field {0} must be between {1} and {2}.")]
        public int? TeamNumber { get; set; } = null!;

        [StringLength(32, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")]
        [RegularExpression(TeamReqex, ErrorMessage = "The {0} field must contain Latin letters, with words separated by a single space or hyphen.")]
        [Display(Name = "Name (team suffix)")]
        public string? TeamSuffix { get; set; } = null!;

        [Required(ErrorMessage = "The {0} field is required.")]
        [StringLength(32, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")]
        [RegularExpression(ParticipantReqex, ErrorMessage = "The {0} field may contain only Russian or Latin letters, digits, spaces or hyphens.")]
        [Display(Name = "Participant 1")]
        public string Participant1 { get; set; } = null!;

        [Required(ErrorMessage = "The {0} field is required.")]
        [StringLength(32, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")]
        [RegularExpression(ParticipantReqex, ErrorMessage = "The {0} field may contain only Russian or Latin letters, digits, spaces or hyphens.")]
        [Display(Name = "Participant 2")]
        public string Participant2 { get; set; } = null!;

        [Required(ErrorMessage = "The {0} field is required.")]
        [StringLength(32, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")]
        [RegularExpression(ParticipantReqex, ErrorMessage = "The {0} field may contain only Russian or Latin letters, digits, spaces or hyphens.")]
        [Display(Name = "Participant 3")]
        public string Participant3 { get; set; } = null!;

        [Display(Name = "I confirm that the data is filled in correctly")]
        public bool Confirmation { get; set; }
    }

    private async Task LoadAsync(int id, int orgId)
    {
        var contest = await _context.Contests
            .SingleOrDefaultAsync(x => x.Id == id && !x.IsHidden);

        var organization = await _context.Organizations
            .Include(x => x.Users.OrderBy(x => x.Name).Where(x => x is OlympUser && !x.IsHidden))
            .SingleOrDefaultAsync(x => x.Id == orgId);

        Contest = contest;
        Organization = organization;
        Participants = organization?.Users.OfType<OlympUser>();
        Input.TeamPrefix ??= Organization?.ACMName;
    }

    public async Task<IActionResult> OnGetAsync(int id, int orgId)
    {
        await LoadAsync(id, orgId);

        if (Contest is null || !Contest.IsAcm || Organization is null)
            return NotFound();

        if (!User.HasClaim(x => x.Type == ClaimNames.CoachOrg && int.Parse(x.Value) == orgId) || DateTimeOffset.Now > Contest.EndTime)
            return Forbid();

        return Page();
    }

    private void Validate()
    {
        if (int.TryParse(Input.Participant1, out var id1) && (Member1 = Participants!.SingleOrDefault(x => x.Id == id1)) is null)
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Participant1)}", _localizer["Invalid value"]);

        if (int.TryParse(Input.Participant2, out var id2) && (Member2 = Participants!.SingleOrDefault(x => x.Id == id2)) is null)
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Participant2)}", _localizer["Invalid value"]);

        if (int.TryParse(Input.Participant3, out var id3) && (Member3 = Participants!.SingleOrDefault(x => x.Id == id3)) is null)
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Participant3)}", _localizer["Invalid value"]);

        var memberIds = new[] { Member1, Member2, Member3 }.OfType<OlympUser>().Select(x => x.Id).ToArray();
        if (memberIds.Distinct().Count() != memberIds.Length)
            ModelState.AddModelError(string.Empty, _localizer["Participants must be different."]);

        if (Input.Participant1 == Input.Participant2 || Input.Participant1 == Input.Participant3 || Input.Participant2 == Input.Participant3)
            ModelState.AddModelError(string.Empty, _localizer["Participants must be different."]);

        if (ModelState.IsValid)
        {
            TeamName = Input.TeamPrefix;
            if (Input.TeamNumber is int x)
                TeamName += $" {x}";
            if (Input.TeamSuffix is not null)
                TeamName += $": {Input.TeamSuffix}";

            static string GetInfo(OlympUser? user, string name) => user is null ? $"{name};" : $"{user.LastName} {user.FirstName};{user.Id}";
            TeamInfo = GetInfo(Member1, Input.Participant1);
            TeamInfo += ";" + GetInfo(Member2, Input.Participant2);
            TeamInfo += ";" + GetInfo(Member3, Input.Participant3);

            static string GetName(OlympUser? user, string name) => user is null ? $"{name}" : $"{user.LastName} {user.FirstName}";
            TeamMembers = GetName(Member1, Input.Participant1);
            TeamMembers += ", " + GetName(Member2, Input.Participant2);
            TeamMembers += ", " + GetName(Member3, Input.Participant3);

            if (!Input.Confirmation)
            {
                NeedConfirmation = true;
                ModelState.AddModelError(string.Empty, _localizer["Please confirm the correctness of the data."]);
            }
        }
    }

    public async Task<IActionResult> OnPostAsync(int id, int orgId)
    {
        await LoadAsync(id, orgId);

        if (Contest is null || !Contest.IsAcm || Organization is null)
            return NotFound();

        if (!User.HasClaim(x => x.Type == ClaimNames.CoachOrg && int.Parse(x.Value) == orgId) || DateTimeOffset.Now > Contest.EndTime)
            return Forbid();

        Validate();

        if (!ModelState.IsValid)
            return Page();

        try
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var memberIds = new[] { Member1, Member2, Member3 }.OfType<OlympUser>().Select(x => x.Id).ToArray();
            if (await _context.Competitors.AnyAsync(x => x.ContestId == id && x.Members.Any(m => memberIds.Contains(m.Id))))
            {
                ModelState.AddModelError(string.Empty, _localizer["One or more participants are already registered for this contest."]);
                return Page();
            }

            var coachId = int.Parse(_userManager.GetUserId(User)!);

            var competitor = new Competitor
            {
                Name = TeamName!,
                Contest = null!,
                ContestId = Contest.Id,
                OrganizationId = orgId,
                CoachId = coachId,
                MemberInfos = TeamInfo,
                RegistrationDate = DateTimeOffset.Now,
                SecurityStamp = Guid.NewGuid().ToString(),
                Members = [],
                Messages = [],
                Submissions = []
            };

            _context.Attach(Organization);
            if (Member1 is not null)
                competitor.Members.Add(Member1);
            if (Member2 is not null)
                competitor.Members.Add(Member2);
            if (Member3 is not null)
                competitor.Members.Add(Member3);

            var result = await _userManager.CreateAsync(competitor);
            if (!result.Succeeded)
                return BadRequest(result.Errors);
            await transaction.CommitAsync();

            return RedirectToPage("./Index", new { id });
        }
        catch (Exception ex) when (ex is DbUpdateException || ex.GetBaseException() is DbException)
        {
            logger.LogError(ex, "Failed to register participants for contest {ContestId}", id);
            return BadRequest(_localizer["Could not save the registration. Please try again."].Value);
        }
    }
}

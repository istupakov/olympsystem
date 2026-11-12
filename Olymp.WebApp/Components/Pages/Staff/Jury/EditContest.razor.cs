using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Components.Pages.Staff.Jury;

public partial class EditContest
{
    [Parameter]
    public int? Id { get; set; }
    [SupplyParameterFromQuery(Name = "saved")]
    public bool ShowSaved { get; set; }

    [CascadingParameter]
    private Task<AuthenticationState> AuthenticationState { get; set; } = default!;

    private InputModel _input = new();
    private EditContext? _editContext;
    private ValidationMessageStore _messages = default!;
    private string? _unavailable;
    private string? _error;
    private bool _preview;
    private bool _saving;
    private bool _saved;
    private bool _isHidden = true;
    private string Title => Id is null ? SiteResources.CreateContest : SiteResources.EditContest;

    protected override async Task OnParametersSetAsync()
    {
        _editContext = null;
        _unavailable = _error = null;
        _saved = ShowSaved;
        _preview = false;
        _input = new();
        _isHidden = true;
        if (Id is { } id)
        {
            await using var context = await ContextFactory.CreateDbContextAsync();
            var user = (await AuthenticationState).User;
            if (!(await Authorization.AuthorizeAsync(user, id, PolicyNames.ManageContest)).Succeeded)
            {
                _unavailable = SiteResources.StaffAccessDenied;
                return;
            }
            var contest = await context.Contests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id);
            if (contest is null)
            {
                _unavailable = SiteResources.PageNotFoundMessage;
                return;
            }
            _input = InputModel.From(contest);
            _isHidden = contest.IsHidden;
        }
        _editContext = new(_input);
        _messages = new(_editContext);
        _editContext.OnFieldChanged += (_, args) =>
        {
            _saved = false;
            _messages.Clear(args.FieldIdentifier);
            _editContext.NotifyValidationStateChanged();
        };
    }

    private async Task SaveAsync()
    {
        if (_saving || _editContext is null)
            return;
        _messages.Clear();
        _error = null;
        _saved = false;
        if (!await _editContext.ValidateAsync())
            return;
        _saving = true;
        try
        {
            var user = (await AuthenticationState).User;
            if (Id is null)
            {
                if (!(await Authorization.AuthorizeAsync(user, PolicyNames.JuryOrAdmin)).Succeeded)
                {
                    _error = SiteResources.StaffAccessDenied;
                    return;
                }
                await CreateAsync(user);
            }
            else
            {
                await using var context = await ContextFactory.CreateDbContextAsync();
                if (!(await Authorization.AuthorizeAsync(user, Id.Value, PolicyNames.ManageContest)).Succeeded)
                {
                    _error = SiteResources.StaffAccessDenied;
                    return;
                }
                var contest = await context.Contests.SingleOrDefaultAsync(item => item.Id == Id.Value);
                if (contest is null)
                {
                    _error = SiteResources.PageNotFoundMessage;
                    return;
                }
                if (!await CheckAbbreviationAsync(context))
                    return;
                _input.ApplyTo(contest);
                await context.SaveChangesAsync();
                _isHidden = contest.IsHidden;
                _saved = true;
            }
        }
        catch (DbUpdateException exception)
        {
            Logger.LogError(exception, "Failed to save contest {ContestId}", Id);
            _error = SiteResources.StaffSaveFailed;
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task CreateAsync(ClaimsPrincipal principal)
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await users.GetUserAsync(principal);
        if (user is null)
        {
            _error = SiteResources.StaffAccessDenied;
            return;
        }
        if (!await CheckAbbreviationAsync(context))
            return;
        await using var transaction = await context.Database.BeginTransactionAsync();
        var contest = new Contest { Name = "", Abbr = "", Problems = [], Competitors = [], Messages = [] };
        _input.ApplyTo(contest);
        context.Contests.Add(contest);
        await context.SaveChangesAsync();
        var result = await users.AddClaimAsync(user, new Claim(ClaimNames.ContestManage, contest.Id.ToString(CultureInfo.InvariantCulture)));
        if (!result.Succeeded)
        {
            _error = SiteResources.StaffSaveFailed;
            return;
        }
        await transaction.CommitAsync();
        Navigation.NavigateTo($"jury/contests/{contest.Id}/edit?saved=true");
    }

    private async Task<bool> CheckAbbreviationAsync(OlympContext context)
    {
        string abbr = _input.Abbr.Trim();
        if (!await context.Contests.AnyAsync(item => item.Id != Id && item.Abbr == abbr))
            return true;
        _messages.Add(_editContext!.Field(nameof(InputModel.Abbr)), SiteResources.AbbreviationInUse);
        _editContext.NotifyValidationStateChanged();
        return false;
    }

    public sealed class InputModel : IValidatableObject
    {
        [Required(ErrorMessageResourceType = typeof(AccountResources), ErrorMessageResourceName = nameof(AccountResources.FieldRequired))]
        [StringLength(256, ErrorMessageResourceType = typeof(AccountResources), ErrorMessageResourceName = nameof(AccountResources.FieldMaximumLength))]
        [Display(Name = nameof(SiteResources.StaffName), ResourceType = typeof(SiteResources))]
        public string Name { get; set; } = "";

        [Required(ErrorMessageResourceType = typeof(AccountResources), ErrorMessageResourceName = nameof(AccountResources.FieldRequired))]
        [StringLength(128, ErrorMessageResourceType = typeof(AccountResources), ErrorMessageResourceName = nameof(AccountResources.FieldMaximumLength))]
        [RegularExpression("[A-Za-z][A-Za-z0-9_-]*", ErrorMessageResourceType = typeof(SiteResources), ErrorMessageResourceName = nameof(SiteResources.AbbreviationFormat))]
        [Display(Name = nameof(SiteResources.Abbreviation), ResourceType = typeof(SiteResources))]
        public string Abbr { get; set; } = "";

        public string? Description { get; set; }

        [Required(ErrorMessageResourceType = typeof(AccountResources), ErrorMessageResourceName = nameof(AccountResources.FieldRequired))]
        [Display(Name = nameof(SiteResources.ContestStart), ResourceType = typeof(SiteResources))]
        public DateTime? StartTime { get; set; } = DateTime.Today.AddDays(1).AddHours(10);

        public string Duration { get; set; } = "5:00";
        public bool EarlyPrinting { get; set; }
        public string PrintingAdvance { get; set; } = "0:30";
        public bool Freeze { get; set; }
        public string FreezeAdvance { get; set; } = "1:00";
        public bool IsHidden { get; set; } = true;
        public bool IsOpen { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            bool validDuration = TryParseInterval(Duration, out var duration) && duration > TimeSpan.Zero;
            bool validPrinting = !EarlyPrinting || TryParseInterval(PrintingAdvance, out _);
            bool validFreeze = !Freeze || (TryParseInterval(FreezeAdvance, out var advance) && (!validDuration || advance <= duration));
            if (!validDuration)
                yield return new(SiteResources.PositiveDurationRequired, [nameof(Duration)]);
            if (!validPrinting)
                yield return new(SiteResources.IntervalFormat, [nameof(PrintingAdvance)]);
            if (!validFreeze)
                yield return new(SiteResources.FreezeIntervalInvalid, [nameof(FreezeAdvance)]);
            if (IsOpen && Freeze)
                yield return new(SiteResources.OpenContestCannotFreeze, [nameof(Freeze)]);
            if (StartTime is { } start)
            {
                if (TimeZoneInfo.Local.IsInvalidTime(start) || TimeZoneInfo.Local.IsAmbiguousTime(start))
                    yield return new(SiteResources.UnambiguousServerTime, [nameof(StartTime)]);
                else if (validDuration && validPrinting && validFreeze && !TryGetSchedule(out _, out _, out _, out _))
                    yield return new(SiteResources.ScheduleOutOfRange, [nameof(StartTime)]);
            }
        }

        public bool TryGetSchedule(out DateTimeOffset start, out DateTimeOffset end,
            out DateTimeOffset? printing, out DateTimeOffset? freeze)
        {
            start = end = default;
            printing = freeze = null;
            TimeSpan printingAdvance = default;
            TimeSpan freezeAdvance = default;
            if (StartTime is not { } date || TimeZoneInfo.Local.IsInvalidTime(date) || TimeZoneInfo.Local.IsAmbiguousTime(date)
                || !TryParseInterval(Duration, out var duration) || duration <= TimeSpan.Zero
                || (EarlyPrinting && !TryParseInterval(PrintingAdvance, out printingAdvance))
                || (Freeze && !TryParseInterval(FreezeAdvance, out freezeAdvance)))
                return false;
            try
            {
                start = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(date));
                end = start.Add(duration);
                if (EarlyPrinting)
                    printing = start.Subtract(printingAdvance);
                if (Freeze)
                    freeze = end.Subtract(freezeAdvance);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        public void ApplyTo(Contest contest)
        {
            if (!TryGetSchedule(out var start, out var end, out var printing, out var freeze))
                throw new InvalidOperationException();
            contest.Name = Name.Trim();
            contest.Abbr = Abbr.Trim();
            contest.Description = Description;
            contest.StartTime = start;
            contest.EndTime = end;
            contest.ProblemShowTime = printing;
            contest.FreezeTime = freeze;
            contest.IsHidden = IsHidden;
            contest.IsOpen = IsOpen;
        }

        public static InputModel From(Contest contest) => new()
        {
            Name = contest.Name,
            Abbr = contest.Abbr,
            Description = contest.Description,
            StartTime = contest.StartTime.LocalDateTime,
            Duration = FormatInterval(contest.EndTime - contest.StartTime),
            EarlyPrinting = contest.ProblemShowTime.HasValue,
            PrintingAdvance = contest.ProblemShowTime is { } printing ? FormatInterval(contest.StartTime - printing) : "0:30",
            Freeze = contest.FreezeTime.HasValue,
            FreezeAdvance = contest.FreezeTime is { } freeze ? FormatInterval(contest.EndTime - freeze) : "1:00",
            IsHidden = contest.IsHidden,
            IsOpen = contest.IsOpen
        };

        private static string FormatInterval(TimeSpan value) =>
            string.Create(CultureInfo.InvariantCulture, $"{(value < TimeSpan.Zero ? "-" : "")}{Math.Abs((long)value.TotalHours)}:{Math.Abs(value.Minutes):00}");

        private static bool TryParseInterval(string? text, out TimeSpan interval)
        {
            interval = default;
            var parts = text?.Split(':');
            if (parts is not { Length: 2 } || parts[1].Length != 2
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long hours)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) || minutes > 59
                || hours > (TimeSpan.MaxValue.Ticks - (minutes * TimeSpan.TicksPerMinute)) / TimeSpan.TicksPerHour)
                return false;
            interval = new TimeSpan((hours * TimeSpan.TicksPerHour) + (minutes * TimeSpan.TicksPerMinute));
            return true;
        }
    }
}

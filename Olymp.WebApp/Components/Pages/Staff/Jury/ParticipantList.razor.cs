using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.QuickGrid;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

namespace Olymp.WebApp.Components.Pages.Staff.Jury;

public partial class ParticipantList
{
    [Parameter]
    public int? ContestId { get; set; }
    [CascadingParameter]
    private Task<AuthenticationState> AuthenticationState { get; set; } = default!;
    private Contest? _contest;
    [SupplyParameterFromQuery] public int? Contest { get; set; }
    [SupplyParameterFromQuery] public int? Coach { get; set; }
    [SupplyParameterFromQuery] public int? Member { get; set; }
    [SupplyParameterFromQuery] public string? CoachSearch { get; set; }
    [SupplyParameterFromQuery] public string? MemberSearch { get; set; }
    private int? SelectedContest => ContestId ?? Contest;
    private string ListUrl => ContestId is int id ? $"jury/contests/{id}/participants" : "jury/participants";
    private List<Contest> _contests = [];
    private string? _coachName, _memberName;
    [SupplyParameterFromQuery] public string? Search { get; set; }
    [SupplyParameterFromQuery] public int? Organization { get; set; }
    [SupplyParameterFromQuery] public bool? Approved { get; set; }
    [SupplyParameterFromQuery] public bool? Unofficial { get; set; }
    [SupplyParameterFromQuery] public bool? Disqualified { get; set; }
    private Filters _filters = new();
    private List<Organization> _organizations = [];
    private int _gridVersion;
    private bool _hasUnapproved, _hasAssignedLogins;
    private bool BeforeEnd => _contest is not null && Clock.GetUtcNow() < _contest.EndTime;
    private bool CanImportParticipants => BeforeEnd && _contest is { IsAcm: false };
    private bool CanImportIcpc => BeforeEnd && _contest is { IsIcpc: true };
    private bool CanAssignLogins { get => BeforeEnd && field; set; }
    private bool CanDeleteEmpty { get => _contest is not null && Clock.GetUtcNow() > _contest.EndTime && field; set; }
    private bool HasOperations => _hasUnapproved || _hasAssignedLogins || CanImportParticipants || CanImportIcpc || CanAssignLogins || CanDeleteEmpty;
    private ExportPreview? _export;
    private bool _preparingExport;
    private sealed record ExportPreview(int Total, int Missing, string Url);
    private readonly GridSort<Competitor> _idSort = GridSort<Competitor>.ByAscending(item => item.Id);
    private readonly GridSort<Competitor> _nameSort = GridSort<Competitor>.ByAscending(item => item.Name);
    private readonly GridSort<Competitor> _dateSort = GridSort<Competitor>.ByAscending(item => item.RegistrationDate);
    private ParticipantQuery Selection => new(SelectedContest, Search, Organization, Approved, Unofficial, Disqualified, Coach, Member, Coach.HasValue ? null : CoachSearch, Member.HasValue ? null : MemberSearch);
    internal IQueryable<Competitor> Query(OlympContext db) => Selection.Apply(db)
        .Include(item => item.Contest).Include(item => item.Organization).Include(item => item.Coach).Include(item => item.Members).AsSplitQuery()
        .OrderByDescending(item => item.Contest.StartTime).ThenBy(item => item.RegistrationDate).ThenBy(item => item.Id);
    private string ParticipantUrl(Competitor participant) => GridState.DetailUrl(Navigation.Uri, $"jury/contests/{participant.ContestId}/participants/{participant.Id}", participant.Id);
    private string AccountUrl(int id, int? sourceId = null) => GridState.DetailUrl(Navigation.Uri, $"admin/users/{id}/edit", sourceId ?? id);
    private string ExportUrl()
    {
        var query = QueryHelpers.ParseQuery(new Uri(Navigation.Uri).Query);
        query.TryGetValue("sort", out var column);
        query.TryGetValue("direction", out var direction);
        string sortKey = column == "ID" ? "ID" : column == SiteResources.StaffName ? "Name"
            : column == SiteResources.StaffLogin ? "Login" : "Date";
        return QueryHelpers.AddQueryString($"/jury/contests/{ContestId}/participants/export", new Dictionary<string, string?>
        {
            ["search"] = Search,
            ["coach"] = Coach?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["member"] = Member?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["coachSearch"] = Coach.HasValue ? null : CoachSearch,
            ["memberSearch"] = Member.HasValue ? null : MemberSearch,
            ["organization"] = Organization?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["approved"] = Approved?.ToString(),
            ["unofficial"] = Unofficial?.ToString(),
            ["disqualified"] = Disqualified?.ToString(),
            ["sort"] = sortKey,
            ["descending"] = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase).ToString()
        });
    }
    private void ApplyFilters() => Navigation.NavigateTo(Navigation.GetUriWithQueryParameters(new Dictionary<string, object?>
    {
        [nameof(Search)] = _filters.Search,
        [nameof(Contest)] = ContestId.HasValue ? null : _filters.Contest,
        [nameof(CoachSearch)] = Coach.HasValue ? null : _filters.CoachSearch,
        [nameof(MemberSearch)] = Member.HasValue ? null : _filters.MemberSearch,
        [nameof(Organization)] = _filters.Organization,
        [nameof(Approved)] = ParseFlag(_filters.Approved),
        [nameof(Unofficial)] = ParseFlag(_filters.Unofficial),
        [nameof(Disqualified)] = ParseFlag(_filters.Disqualified),
        ["row"] = null
    }));
    private static bool? ParseFlag(string? value) => bool.TryParse(value, out bool flag) ? flag : null;
    private sealed class Filters
    {
        public int? Contest { get; set; }
        public string? CoachSearch { get; set; }
        public string? MemberSearch { get; set; }
        public string? Search { get; set; }
        public int? Organization { get; set; }
        public string? Approved { get; set; }
        public string? Unofficial { get; set; }
        public string? Disqualified { get; set; }
    }
    private bool _loaded;
    private bool _canManage;
    private bool _admin;
    private bool _busy;
    private string? _error;
    private string? _status;
    private Pending? _pending;
    private string[] _names = [];
    private readonly Dictionary<string, string> _logins = [with(StringComparer.OrdinalIgnoreCase)];

    protected override async Task OnParametersSetAsync()
    {
        _filters = new() { Contest = Contest, CoachSearch = CoachSearch, MemberSearch = MemberSearch, Search = Search, Organization = Organization, Approved = Approved?.ToString(), Unofficial = Unofficial?.ToString(), Disqualified = Disqualified?.ToString() };
        _pending = null;
        _export = null;
        _names = [];
        _logins.Clear();
        _error = _status = null;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        await using var db = await ContextFactory.CreateDbContextAsync();
        _contest = await db.Contests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == ContestId);
        var user = (await AuthenticationState).User;
        _admin = user.IsInRole(RoleNames.Admin);
        _canManage = ContestId is int scopedId && (await Authorization.AuthorizeAsync(user, scopedId, PolicyNames.ManageContest)).Succeeded;
        _contests = ContestId.HasValue ? [] : await db.Contests.AsNoTracking().OrderByDescending(item => item.StartTime).ThenByDescending(item => item.Id).ToListAsync();
        _coachName = Coach is int coach ? StaffEntityLabel.Format(coach, await db.OlympUsers.Where(item => item.Id == coach).Select(item => item.Name).SingleOrDefaultAsync()) : null;
        _memberName = Member is int member ? StaffEntityLabel.Format(member, await db.OlympUsers.Where(item => item.Id == member).Select(item => item.Name).SingleOrDefaultAsync()) : null;
        await LoadOrganizationsAsync();
        _hasUnapproved = _hasAssignedLogins = CanAssignLogins = CanDeleteEmpty = false;
        if (_canManage)
        {
            var participants = db.Competitors.Where(item => item.ContestId == ContestId);
            _hasUnapproved = await participants.AnyAsync(item => !item.IsApproved);
            _hasAssignedLogins = await participants.AnyAsync(item => item.UserName != null && item.UserName != "");
            CanAssignLogins = await participants.AnyAsync(item => item.UserName == null);
            CanDeleteEmpty = await participants.AnyAsync(item => !item.Submissions.Any() && !item.Messages.Any());
        }
        _gridVersion++;
        _loaded = true;
    }

    private Task SelectContestAsync() => LoadOrganizationsAsync();

    private async Task LoadOrganizationsAsync()
    {
        var filters = _filters;
        int? contest = ContestId ?? filters.Contest;
        await using var db = await ContextFactory.CreateDbContextAsync();
        var organizations = await db.Competitors.Where(item => (contest == null || item.ContestId == contest) && item.OrganizationId != null)
            .Select(item => item.Organization!).Distinct().OrderBy(item => item.Name).ToListAsync();
        if (filters.Organization is int organizationId && !organizations.Any(item => item.Id == organizationId))
        {
            var selected = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == organizationId);
            if (selected is not null) organizations.Add(selected);
        }
        if (ReferenceEquals(filters, _filters) && contest == (ContestId ?? _filters.Contest)) _organizations = organizations;
    }

    private async Task PrepareExportAsync()
    {
        if (!ContestId.HasValue || _preparingExport) return;
        string uri = Navigation.Uri, url = ExportUrl();
        var selection = Selection;
        _preparingExport = true;
        try
        {
            await using var db = await ContextFactory.CreateDbContextAsync();
            var query = selection.Apply(db);
            int total = await query.CountAsync();
            int missing = await query.CountAsync(item => item.UserName == null || item.UserName == "");
            if (Navigation.Uri == uri) _export = new(total, missing, url);
        }
        catch (System.Data.Common.DbException exception)
        {
            Logger.LogError(exception, "Failed to prepare participant export in contest {ContestId}", ContestId);
            _error = SiteResources.StaffSaveFailed;
        }
        finally { _preparingExport = false; }
    }

    private Task Ask(ParticipantAction action, Competitor? participant = null)
    {
        _error = _status = null;
        if (ContestId is not int contestId) return Task.CompletedTask;
        if (participant is not null && participant.ContestId != contestId) return Task.CompletedTask;
        _pending = new(action, contestId, participant?.Id, participant?.Name);
        return action is ParticipantAction.Approve or ParticipantAction.Disapprove ? ExecuteAsync() : Task.CompletedTask;
    }

    private async Task SelectParticipantsAsync(InputFileChangeEventArgs args)
    {
        if (_busy) return;
        _busy = true;
        _error = null;
        _pending = null;
        try
        {
            using var reader = new StreamReader(args.File.OpenReadStream());
            var names = new List<string>();
            while (await reader.ReadLineAsync() is { } line)
                if (!string.IsNullOrWhiteSpace(line)) names.Add(line.Trim());
            _names = [.. names];
            await Ask(ParticipantAction.ImportParticipants);
        }
        catch (IOException) { _error = SiteResources.StaffFileInvalid; }
        finally { _busy = false; }
    }

    private async Task SelectLoginsAsync(InputFileChangeEventArgs args)
    {
        if (_busy) return;
        _busy = true;
        _error = null;
        _pending = null;
        _logins.Clear();
        try
        {
            using var reader = new StreamReader(args.File.OpenReadStream());
            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(';', '\t', ',');
                if (parts is not [var login, var password] || string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password)
                    || !_logins.TryAdd(login.Trim(), password.Trim()))
                {
                    _logins.Clear();
                    _error = SiteResources.StaffLoginsFileInvalid;
                    return;
                }
            }
            if (_logins.Count == 0) { _error = SiteResources.StaffLoginsFileInvalid; return; }
            await Ask(ParticipantAction.ImportLogins);
        }
        catch (IOException) { _error = SiteResources.StaffFileInvalid; }
        finally { _busy = false; }
    }

    private async Task ExecuteAsync()
    {
        if (_busy || _pending is not { } pending) return;
        _busy = true;
        _error = _status = null;
        int contestId = pending.ContestId;
        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            var actor = (await AuthenticationState).User;
            var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
            switch (pending.Action)
            {
                case ParticipantAction.Approve: await service.SetApprovalAsync(actor, contestId, pending.Id!.Value, true); break;
                case ParticipantAction.Disapprove: await service.SetApprovalAsync(actor, contestId, pending.Id!.Value, false); break;
                case ParticipantAction.ApproveAll: await service.ApproveAllAsync(actor, contestId); break;
                case ParticipantAction.GenerateLogins: await service.GenerateLoginsAsync(actor, contestId); break;
                case ParticipantAction.RemoveLogins: await service.RemoveLoginsAsync(actor, contestId); break;
                case ParticipantAction.DeleteEmpty:
                    _status = string.Format(SiteResources.StaffDeletedCount, await service.DeleteWithoutSubmissionsAsync(actor, contestId)); break;
                case ParticipantAction.ImportParticipants:
                    var summary = await service.ImportAsync(actor, contestId, _names);
                    _status = string.Format(SiteResources.StaffImportedCount, summary.Added, summary.Existing); break;
                case ParticipantAction.ImportLogins: await service.AssignLoginsAsync(actor, contestId, _logins); break;
            }
            _status ??= SiteResources.StaffSaved;
        }
        catch (EntityNotFoundException) { _error = SiteResources.PageNotFoundMessage; }
        catch (OperationValidationException exception) { _error = exception.Message; }
        catch (IdentityOperationException exception) { _error = string.Join(" ", exception.Errors.Select(item => item.Description)); }
        catch (Microsoft.Data.SqlClient.SqlException exception) { Logger.LogError(exception, "Participant operation failed in contest {ContestId}", contestId); _error = SiteResources.StaffSaveFailed; }
        catch (DbUpdateException exception) { Logger.LogError(exception, "Participant operation failed in contest {ContestId}", contestId); _error = SiteResources.StaffSaveFailed; }
        finally { _pending = null; _names = []; _logins.Clear(); _busy = false; }
        _export = null;
        await LoadAsync();
    }

    private static string OperationName(ParticipantAction action) => action switch
    {
        ParticipantAction.Approve => SiteResources.StaffApprove,
        ParticipantAction.Disapprove => SiteResources.StaffDisapprove,
        ParticipantAction.ApproveAll => SiteResources.StaffApproveAll,
        ParticipantAction.GenerateLogins => SiteResources.StaffGenerateLogins,
        ParticipantAction.RemoveLogins => SiteResources.StaffRemoveLogins,
        ParticipantAction.DeleteEmpty => SiteResources.StaffDeleteEmptyParticipants,
        ParticipantAction.ImportParticipants => SiteResources.StaffImportParticipants,
        ParticipantAction.ImportLogins => SiteResources.StaffImportLogins,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
    private enum ParticipantAction { Approve, Disapprove, ApproveAll, GenerateLogins, RemoveLogins, DeleteEmpty, ImportParticipants, ImportLogins }
    private sealed record Pending(ParticipantAction Action, int ContestId, int? Id, string? Name);
}

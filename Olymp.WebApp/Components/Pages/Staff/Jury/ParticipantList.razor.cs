using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

namespace Olymp.WebApp.Components.Pages.Staff.Jury;

public partial class ParticipantList
{
    [Parameter]
    public int ContestId { get; set; }
    [CascadingParameter]
    private Task<AuthenticationState> AuthenticationState { get; set; } = default!;
    private Contest? _contest;
    private List<Competitor> _participants = [];
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
        _pending = null;
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
        _canManage = (await Authorization.AuthorizeAsync(user, ContestId, PolicyNames.ManageContest)).Succeeded;
        _participants = _contest is null ? [] : await db.Competitors.AsNoTracking().Where(item => item.ContestId == ContestId)
            .Include(item => item.Organization).Include(item => item.Coach).Include(item => item.Members).AsSplitQuery()
            .OrderBy(item => item.RegistrationDate).ThenBy(item => item.Id).ToListAsync();
        _loaded = true;
    }

    private Task Ask(ParticipantAction action, Competitor? participant = null)
    {
        _error = _status = null;
        _pending = new(action, participant?.Id, participant?.Name);
        return action is ParticipantAction.Approve or ParticipantAction.Disapprove or ParticipantAction.Official or ParticipantAction.Unofficial
            or ParticipantAction.Disqualify or ParticipantAction.Restore ? ExecuteAsync() : Task.CompletedTask;
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
        int contestId = ContestId;
        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            var actor = (await AuthenticationState).User;
            var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
            switch (pending.Action)
            {
                case ParticipantAction.Approve: await service.SetApprovalAsync(actor, contestId, pending.Id!.Value, true); break;
                case ParticipantAction.Disapprove: await service.SetApprovalAsync(actor, contestId, pending.Id!.Value, false); break;
                case ParticipantAction.Official: await service.SetOutOfCompetitionAsync(actor, contestId, pending.Id!.Value, false); break;
                case ParticipantAction.Unofficial: await service.SetOutOfCompetitionAsync(actor, contestId, pending.Id!.Value, true); break;
                case ParticipantAction.Disqualify: await service.SetDisqualificationAsync(actor, contestId, pending.Id!.Value, true); break;
                case ParticipantAction.Restore: await service.SetDisqualificationAsync(actor, contestId, pending.Id!.Value, false); break;
                case ParticipantAction.Delete: await service.DeleteAsync(actor, contestId, pending.Id!.Value); break;
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
        await LoadAsync();
    }

    private static string OperationName(ParticipantAction action) => action switch
    {
        ParticipantAction.Approve => SiteResources.StaffApprove,
        ParticipantAction.Disapprove => SiteResources.StaffDisapprove,
        ParticipantAction.Official => SiteResources.StaffMakeOfficial,
        ParticipantAction.Unofficial => SiteResources.StaffMakeUnofficial,
        ParticipantAction.Disqualify => SiteResources.StaffDisqualify,
        ParticipantAction.Restore => SiteResources.StaffRestoreParticipant,
        ParticipantAction.Delete => SiteResources.StaffDelete,
        ParticipantAction.ApproveAll => SiteResources.StaffApproveAll,
        ParticipantAction.GenerateLogins => SiteResources.StaffGenerateLogins,
        ParticipantAction.RemoveLogins => SiteResources.StaffRemoveLogins,
        ParticipantAction.DeleteEmpty => SiteResources.StaffDeleteEmptyParticipants,
        ParticipantAction.ImportParticipants => SiteResources.StaffImportParticipants,
        ParticipantAction.ImportLogins => SiteResources.StaffImportLogins,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
    private enum ParticipantAction { Approve, Disapprove, Official, Unofficial, Disqualify, Restore, Delete, ApproveAll, GenerateLogins, RemoveLogins, DeleteEmpty, ImportParticipants, ImportLogins }
    private sealed record Pending(ParticipantAction Action, int? Id, string? Name);
}

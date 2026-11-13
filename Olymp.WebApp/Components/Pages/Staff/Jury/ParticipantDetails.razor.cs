using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

namespace Olymp.WebApp.Components.Pages.Staff.Jury;

public partial class ParticipantDetails
{
    [Parameter] public int ContestId { get; set; }
    [Parameter] public int Id { get; set; }
    [SupplyParameterFromQuery] public bool Edit { get; set; }
    private bool Editing => Edit && _admin;
    private string ModeUrl(bool edit) => Navigation.GetUriWithQueryParameter("edit", edit ? true : (bool?)null);
    [SupplyParameterFromQuery] public string? ReturnTo { get; set; }
    [CascadingParameter] private Task<AuthenticationState> AuthenticationState { get; set; } = default!;
    private Competitor? _participant;
    private bool _loaded, _admin, _canManage, _confirmed, _busy, _saved;
    private int _submissionCount, _messageCount;
    private string? _error;
    private string _participantName = "";
    private int? _coachId, _memberId, _memberSlot, _removeMemberId;
    private ParticipantAction? _pending;
    private bool Locked => _participant is { LockoutEnabled: true, LockoutEnd: { } until } && until > DateTimeOffset.Now;
    private string BackUrl => StaffGridState.BackUrl(ReturnTo, $"jury/contests/{ContestId}/participants");

    protected override async Task OnParametersSetAsync()
    {
        _pending = null;
        _error = null;
        _saved = false;
        await LoadAsync();
        if (Edit && !_admin) _error = SiteResources.StaffAccessDenied;
    }

    private async Task LoadAsync()
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        _participant = await db.Competitors.AsNoTracking().Include(item => item.Contest)
            .Include(item => item.Organization).Include(item => item.Coach).Include(item => item.Members)
            .AsSplitQuery().SingleOrDefaultAsync(item => item.Id == Id && item.ContestId == ContestId);
        var actor = (await AuthenticationState).User;
        _admin = (await Authorization.AuthorizeAsync(actor, PolicyNames.Admin)).Succeeded;
        _canManage = (await Authorization.AuthorizeAsync(actor, ContestId, PolicyNames.ManageContest)).Succeeded;
        _submissionCount = await db.Submissions.CountAsync(item => item.UserId == Id);
        _messageCount = await db.Messages.CountAsync(item => item.UserId == Id);
        _participantName = _participant?.Name ?? "";
        _coachId = _participant?.CoachId;
        _memberId = _memberSlot = _removeMemberId = null;
        _confirmed = _participant is not null && await scope.ServiceProvider.GetRequiredService<IUserConfirmation<User>>()
            .IsConfirmedAsync(scope.ServiceProvider.GetRequiredService<UserManager<User>>(), _participant);
        _loaded = true;
    }
    private Task RenameParticipantAsync()
    {
        string name = _participantName;
        return ChangeParticipantAsync((service, actor, id) => service.RenameAsync(actor, id, name));
    }
    private Task SaveCoachAsync()
    {
        int? coachId = _coachId;
        return ChangeParticipantAsync((service, actor, id) => service.SetCoachAsync(actor, id, coachId));
    }
    private Task AddMemberAsync()
    {
        int? slot = _memberSlot;
        return _memberId is int memberId
            ? ChangeParticipantAsync((service, actor, id) => service.AddMemberAsync(actor, id, memberId, slot)) : Task.CompletedTask;
    }
    private Task RemoveMemberAsync(int memberId) => _removeMemberId == memberId
        ? ChangeParticipantAsync((service, actor, id) => service.RemoveMemberAsync(actor, id, memberId)) : Task.CompletedTask;
    private async Task ChangeParticipantAsync(Func<AdminParticipantService, ClaimsPrincipal, int, Task> change)
    {
        if (_busy) return;
        int id = Id, contestId = ContestId;
        _busy = true;
        _saved = false;
        _error = null;
        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            if (!await db.Competitors.AnyAsync(item => item.Id == id && item.ContestId == contestId)) throw new EntityNotFoundException();
            await change(scope.ServiceProvider.GetRequiredService<AdminParticipantService>(), (await AuthenticationState).User, id);
            if (Id != id || ContestId != contestId) return;
            _saved = true;
            await LoadAsync();
        }
        catch (OperationValidationException exception) { _error = exception.Message; }
        catch (EntityNotFoundException) { _error = SiteResources.StaffUserNotFound; }
        catch (Exception exception) { Logger.LogError(exception, "Participant edit failed for {UserId}", Id); _error = SiteResources.StaffSaveFailed; }
        finally { _busy = false; }
    }

    private void Ask(ParticipantAction action) { _pending = action; _saved = false; _error = null; }
    private async Task ExecuteAsync()
    {
        if (_pending is not { } action || _busy) return;
        int id = Id, contestId = ContestId;
        _busy = true;
        _saved = false;
        _error = null;
        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            if (!await db.Competitors.AnyAsync(item => item.Id == id && item.ContestId == contestId)) throw new EntityNotFoundException();
            var actor = (await AuthenticationState).User;
            var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
            switch (action)
            {
                case ParticipantAction.Approve: await service.SetApprovalAsync(actor, contestId, id, true); break;
                case ParticipantAction.Disapprove: await service.SetApprovalAsync(actor, contestId, id, false); break;
                case ParticipantAction.Official: await service.SetOutOfCompetitionAsync(actor, contestId, id, false); break;
                case ParticipantAction.Unofficial: await service.SetOutOfCompetitionAsync(actor, contestId, id, true); break;
                case ParticipantAction.Disqualify: await service.SetDisqualificationAsync(actor, contestId, id, true); break;
                case ParticipantAction.Restore: await service.SetDisqualificationAsync(actor, contestId, id, false); break;
                case ParticipantAction.Delete: await service.DeleteAsync(actor, contestId, id); break;
                case ParticipantAction.Ban:
                case ParticipantAction.Unban:
                    await scope.ServiceProvider.GetRequiredService<AdminParticipantService>().SetLoginBlockedAsync(actor, id, action == ParticipantAction.Ban);
                    break;
            }
            if (Id != id || ContestId != contestId) return;
            if (action == ParticipantAction.Delete) { Navigation.NavigateTo(BackUrl); return; }
            _saved = true;
            await LoadAsync();
        }
        catch (OperationValidationException exception) { _error = exception.Message; }
        catch (EntityNotFoundException) { _error = SiteResources.PageNotFoundMessage; }
        catch (IdentityOperationException exception) { _error = string.Join(" ", exception.Errors.Select(item => item.Description)); }
        catch (Exception exception) { Logger.LogError(exception, "Participant operation {Action} failed for {ParticipantId}", action, id); _error = SiteResources.StaffSaveFailed; }
        finally { _busy = false; _pending = null; }
    }

    private static string ActionName(ParticipantAction action) => action switch
    {
        ParticipantAction.Approve => SiteResources.StaffApprove,
        ParticipantAction.Disapprove => SiteResources.StaffDisapprove,
        ParticipantAction.Official => SiteResources.StaffMakeOfficial,
        ParticipantAction.Unofficial => SiteResources.StaffMakeUnofficial,
        ParticipantAction.Disqualify => SiteResources.StaffDisqualify,
        ParticipantAction.Restore => SiteResources.StaffRestoreParticipant,
        ParticipantAction.Delete => SiteResources.StaffDelete,
        ParticipantAction.Ban => SiteResources.StaffBan,
        ParticipantAction.Unban => SiteResources.StaffUnban,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
    private enum ParticipantAction { Approve, Disapprove, Official, Unofficial, Disqualify, Restore, Delete, Ban, Unban }
}

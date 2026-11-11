using System.Globalization;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services;

namespace Olymp.WebApp.Components.Pages.Staff.Admin;

public partial class EditUser
{
    [Parameter]
    public int Id { get; set; }
    [CascadingParameter]
    private Task<AuthenticationState> AuthenticationState { get; set; } = default!;
    private User? _user;
    private IList<string> _roles = [];
    private List<string> _coachOrganizations = [];
    private bool _loaded;
    private bool _busy;
    private bool _saved;
    private string? _error;
    private UserAction? _pending;
    private bool Locked => _user is { LockoutEnabled: true, LockoutEnd: { } until } && until > DateTimeOffset.Now;

    protected override async Task OnParametersSetAsync() { _pending = null; _saved = false; _error = null; await LoadAsync(); }
    private async Task LoadAsync()
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        _user = await db.Users.AsTracking().Include(item => item.Organization).Include(item => item.Coach).SingleOrDefaultAsync(item => item.Id == Id);
        if (_user is Competitor competitor)
        {
            await db.Entry(competitor).Reference(item => item.Contest).LoadAsync();
            await db.Entry(competitor).Collection(item => item.Members).LoadAsync();
        }
        else if (_user is OlympUser permanent)
        {
            await db.Entry(permanent).Collection(item => item.Memberships).Query().Include(item => item.Contest).LoadAsync();
        }
        _roles = _user is null ? [] : await users.GetRolesAsync(_user);
        var claims = _user is null ? [] : await users.GetClaimsAsync(_user);
        var organizations = await db.Organizations.AsNoTracking().ToDictionaryAsync(item => item.Id, item => item.Name);
        _coachOrganizations = [.. claims.Where(item => item.Type == ClaimNames.CoachOrg).Select(item => int.TryParse(item.Value, out int id)
            && organizations.TryGetValue(id, out var name) ? name : item.Value)];
        _loaded = true;
    }
    private void Ask(UserAction action) { _pending = action; _saved = false; _error = null; }
    private async Task ExecuteAsync()
    {
        if (_busy || _pending is not { } action) return;
        _busy = true;
        _saved = false;
        _error = null;
        int id = Id;
        try
        {
            if (!(await Authorization.AuthorizeAsync((await AuthenticationState).User, PolicyNames.Admin)).Succeeded) { _error = SiteResources.StaffAccessDenied; return; }
            await using var scope = ScopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = await users.FindByIdAsync(id.ToString(CultureInfo.InvariantCulture));
            if (user is null) { _error = SiteResources.StaffUserNotFound; return; }
            if (user is not OlympUser && action is not (UserAction.Ban or UserAction.Unban)) { _error = SiteResources.StaffAccessDenied; return; }
            await using var transaction = await db.Database.BeginTransactionAsync();
            switch (action)
            {
                case UserAction.AddAdmin:
                case UserAction.AddJury:
                    string addedRole = action == UserAction.AddAdmin ? RoleNames.Admin : RoleNames.Jury;
                    Check(await users.AddToRoleAsync(user, addedRole));
                    user.IsHidden = true;
                    Check(await users.UpdateAsync(user));
                    break;
                case UserAction.RemoveAdmin: Check(await users.RemoveFromRoleAsync(user, RoleNames.Admin)); break;
                case UserAction.RemoveJury: Check(await users.RemoveFromRoleAsync(user, RoleNames.Jury)); break;
                case UserAction.AddCoach:
                    if (user.OrganizationId is not { } organizationId) { _error = SiteResources.StaffOrganizationRequired; return; }
                    var claims = await users.GetClaimsAsync(user);
                    string value = organizationId.ToString(CultureInfo.InvariantCulture);
                    if (!claims.Any(item => item.Type == ClaimNames.CoachOrg && item.Value == value))
                        Check(await users.AddClaimAsync(user, new Claim(ClaimNames.CoachOrg, value)));
                    break;
                case UserAction.RemoveCoach:
                    var coachClaims = await users.GetClaimsAsync(user);
                    Check(await users.RemoveClaimsAsync(user, coachClaims.Where(item => item.Type == ClaimNames.CoachOrg)));
                    break;
                case UserAction.ToggleHidden: user.IsHidden = !user.IsHidden; Check(await users.UpdateAsync(user)); break;
                case UserAction.SyncLogin: Check(await users.SetUserNameAsync(user, user.Email)); break;
                case UserAction.Ban: Check(await users.SetLockoutEnabledAsync(user, true)); Check(await users.SetLockoutEndDateAsync(user, DateTimeOffset.Now.AddYears(100))); break;
                case UserAction.Unban: Check(await users.SetLockoutEndDateAsync(user, null)); break;
            }
            if (action is UserAction.Ban or UserAction.RemoveAdmin or UserAction.RemoveJury or UserAction.RemoveCoach)
                Check(await users.UpdateSecurityStampAsync(user));
            await transaction.CommitAsync();
            _saved = true;
        }
        catch (IdentityOperationException exception) { _error = string.Join(" ", exception.Errors.Select(item => item.Description)); }
        catch (Exception exception) { Logger.LogError(exception, "Account operation {Action} failed for {UserId}", action, id); _error = SiteResources.StaffSaveFailed; }
        finally { _pending = null; _busy = false; }
        await LoadAsync();
    }
    private static void Check(IdentityResult result) { if (!result.Succeeded) throw new IdentityOperationException(result.Errors); }
    private static string ActionName(UserAction action) => action switch
    {
        UserAction.AddAdmin => SiteResources.StaffAddAdmin,
        UserAction.RemoveAdmin => SiteResources.StaffRemoveAdmin,
        UserAction.AddJury => SiteResources.StaffAddJury,
        UserAction.RemoveJury => SiteResources.StaffRemoveJury,
        UserAction.AddCoach => SiteResources.StaffAddCoach,
        UserAction.RemoveCoach => SiteResources.StaffRemoveCoach,
        UserAction.ToggleHidden => SiteResources.StaffToggleHidden,
        UserAction.SyncLogin => SiteResources.StaffSyncLogin,
        UserAction.Ban => SiteResources.StaffBan,
        UserAction.Unban => SiteResources.StaffUnban,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
    private enum UserAction { AddAdmin, RemoveAdmin, AddJury, RemoveJury, AddCoach, RemoveCoach, ToggleHidden, SyncLogin, Ban, Unban }
}

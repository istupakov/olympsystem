using System.Security.Claims;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Pages.Users;

public class ManageModel(OlympContext context, UserManager<User> userManager,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    private readonly OlympContext _context = context;
    private readonly UserManager<User> _userManager = userManager;

    public User TargetUser { get; private set; } = null!;
    public Dictionary<int, Organization> Organizations { get; private set; } = null!;
    public IEnumerable<string> Roles { get; private set; } = null!;
    public IEnumerable<Claim> Claims { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int? id, string? username, string? email)
    {
        User? user = null;
        if (id is int userId)
            user = await _userManager.FindByIdAsync(userId.ToString());
        else if (username is not null)
            user = await _userManager.FindByNameAsync(username);
        else if (email is not null)
        {
            var normalizedEmail = _userManager.NormalizeEmail(email);
            var users = await _userManager.Users.Where(x => x.NormalizedEmail == normalizedEmail).Take(2).ToListAsync();
            if (users.Count > 1)
                return BadRequest(localizer["More than one account was found with this email. Specify the username or user ID."].Value);
            user = users.SingleOrDefault();
        }

        if (user is null)
            return NotFound();

        if (id is null)
            return RedirectToPage(new { id = user.Id });

        _context.Attach(user);
        await _context.Entry(user).Reference(x => x.Organization).LoadAsync();
        await _context.Entry(user).Reference(x => x.Coach).LoadAsync();

        if (user is Competitor competitor)
        {
            await _context.Entry(competitor).Reference(x => x.Contest).LoadAsync();
            await _context.Entry(competitor).Collection(x => x.Members).LoadAsync();
        }

        if (user is OlympUser olympUser)
        {
            await _context.Entry(olympUser)
                .Collection(x => x.Memberships)
                .Query()
                .Include(x => x.Contest)
                .LoadAsync();
        }

        TargetUser = user;
        Organizations = await _context.Organizations.ToDictionaryAsync(x => x.Id);
        Roles = await _userManager.GetRolesAsync(user);
        Claims = await _userManager.GetClaimsAsync(user);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, string action, string? role)
    {
        var user = await _userManager.FindByIdAsync(id.ToString());

        if (user is null)
            return NotFound();

        switch (action)
        {
            case "AddRole":
                if (role is not null)
                {
                    var res = await _userManager.AddToRoleAsync(user, role);
                    if (!res.Succeeded)
                        return BadRequest(res.Errors);
                }

                if (role is RoleNames.Admin or RoleNames.Jury)
                {
                    user.IsHidden = true;
                    var res = await _userManager.UpdateAsync(user);
                    if (!res.Succeeded)
                        return BadRequest(res.Errors);
                }
                break;
            case "RemoveRole":
                if (role is not null)
                {
                    var res = await _userManager.RemoveFromRoleAsync(user, role);
                    if (!res.Succeeded)
                        return BadRequest(res.Errors);
                }
                break;
            case "AddCoachOrgClaim":
                if (user.OrganizationId is int orgId)
                {
                    var res = await _userManager.AddClaimAsync(user, new Claim(ClaimNames.CoachOrg, orgId.ToString()));
                    if (!res.Succeeded)
                        return BadRequest(res.Errors);
                }
                break;
            case "RemoveCoachOrgClaim":
                {
                    var claims = await _userManager.GetClaimsAsync(user);
                    var res = await _userManager.RemoveClaimsAsync(
                        user, claims.Where(x => x.Type == ClaimNames.CoachOrg));
                    if (!res.Succeeded)
                        return BadRequest(res.Errors);
                }
                break;
            case "ToogleIsHidden":
                {
                    user.IsHidden = !user.IsHidden;
                    var res = await _userManager.UpdateAsync(user);
                    if (!res.Succeeded)
                        return BadRequest(res.Errors);
                }
                break;
            case "SyncUsername":
                {
                    var res = await _userManager.SetUserNameAsync(user, user.Email);
                    if (!res.Succeeded)
                        return BadRequest(res.Errors);
                }
                break;
            case "Ban":
                {
                    var res = await _userManager.SetLockoutEnabledAsync(user, true);
                    if (!res.Succeeded)
                        return BadRequest(res.Errors);
                    res = await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.Now.AddYears(100));
                    if (!res.Succeeded)
                        return BadRequest(res.Errors);
                }
                break;
            case "Unban":
                {
                    var res = await _userManager.SetLockoutEndDateAsync(user, null);
                    if (!res.Succeeded)
                        return BadRequest(res.Errors);
                }
                break;
        }

        if (action is "Ban" or "RemoveCoachOrgClaim" || (action == "RemoveRole" && role is not null))
        {
            var res = await _userManager.UpdateSecurityStampAsync(user);
            if (!res.Succeeded)
                return BadRequest(res.Errors);
        }

        return RedirectToPage();
    }
}

using System.Data;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Pages.Users;

public class IndexModel(OlympContext context, UserManager<User> userManager) : PageModel
{
    private readonly OlympContext _context = context;
    private readonly UserManager<User> _userManager = userManager;

    public bool AllUsers { get; private set; }
    public List<(string, IList<User>)> UserGroups { get; } = new();

    public async Task OnGetAsync(bool all = false, string? email = null)
    {
        if (email is not null)
        {
            UserGroups.Add((email, await _userManager.Users.Where(x => x.Email == email).ToListAsync()));
            return;
        }

        AllUsers = all;
        var users = (await _userManager.Users.Where(x => x is OlympUser).ToListAsync()).ToHashSet();

        var coachIds = (await _context.UserClaims
            .Where(x => x.ClaimType == ClaimNames.CoachOrg)
            .Select(x => x.UserId)
            .ToListAsync()).ToHashSet();

        var admins = (await _userManager.GetUsersInRoleAsync(RoleNames.Admin)).OrderBy(x => x.Name).ToList();
        var jury = (await _userManager.GetUsersInRoleAsync(RoleNames.Jury)).OrderBy(x => x.Name).ToList();
        var coaches = users.Where(x => coachIds.Contains(x.Id)).OrderBy(x => x.Name).ToList();

        UserGroups.Add(("Admins", admins));
        users.ExceptWith(admins);

        UserGroups.Add(("Jury", jury));
        users.ExceptWith(jury);

        UserGroups.Add(("Coaches (claims)", coaches));
        users.ExceptWith(coaches);

        if (all)
            UserGroups.Add(("Others", users.OrderBy(x => x.Name).ToList()));
    }
}

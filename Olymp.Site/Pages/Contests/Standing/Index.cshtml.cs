using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Pages.Contests.Standing
{
    public class IndexModel(OlympContext context) : PageModel
    {
        private readonly OlympContext _context = context;

        public Contest Contest { get; private set; } = null!;
        public Domain.Standing Standing { get; private set; } = null!;

        public async Task<IActionResult> OnGetAsync(int id, bool official = false, DateTimeOffset? startTime = null)
        {
            var contest = await _context.Contests
                .Include(x => x.FinalTable)
                .Include(x => x.OfficialTable)
                .Include(x => x.Competitors)
                .ThenInclude(x => x.Members)
                .Include(x => x.Problems)
                .ThenInclude(x => x.Submissions)
                .ThenInclude(x => x.User)
                .AsSplitQuery()
                .SingleOrDefaultAsync(x => x.Id == id);

            if (contest is null)
                return NotFound();

            if (contest.AllowShowStanding || User.IsInRole(RoleNames.Admin) || User.IsInRole(RoleNames.Jury))
            {
                Contest = contest;
                var notFreeze = User.IsInRole(RoleNames.Admin) || User.IsInRole(RoleNames.Jury);
                Standing = Domain.Standing.Create(contest, !official, official, notFreeze, startTime);
                ViewData["Table"] = official ? contest.OfficialTableText : contest.FinalTableText;
                ViewData["IsSchool"] = contest.IsSchool;

                return Page();
            }

            return Forbid();
        }
    }
}

using System.Net.Mime;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;

namespace Olymp.Site.Pages.Contests;

public class DownloadTestsModel : PageModel
{
    private readonly OlympContext _context;

    public DownloadTestsModel(OlympContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var contest = await _context.Contests
            .Include(x => x.TestZip)
            .SingleOrDefaultAsync(x => x.Id == id);

        if (contest?.TestZip is null)
            return NotFound();

        if (contest.AllowDownloadTests || User.IsInRole(RoleNames.Admin) || User.IsInRole(RoleNames.Jury))
            return File(contest.TestZip.Data, MediaTypeNames.Application.Zip, $"{contest.Abbr}tests.zip");
        return Forbid();
    }
}

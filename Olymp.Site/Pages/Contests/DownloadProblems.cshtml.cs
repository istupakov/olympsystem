using System.Net.Mime;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;

namespace Olymp.Site.Pages.Contests;

public class DownloadProblemsModel(OlympContext context) : PageModel
{
    private readonly OlympContext _context = context;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var contest = await _context.Contests
            .Include(x => x.ProblemPdf)
            .SingleOrDefaultAsync(x => x.Id == id);

        if (contest?.ProblemPdf is null)
            return NotFound();

        if (contest.AllowDownloadProblems || User.IsInRole(RoleNames.Admin) || User.IsInRole(RoleNames.Jury))
            return File(contest.ProblemPdf.Data, MediaTypeNames.Application.Pdf, $"{contest.Abbr}problems.pdf");
        return Forbid();
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;

namespace Olymp.Site.Pages.Submissions;

public class ErrorsModel(OlympContext context, IAuthorizationService authorizationService) : PageModel
{
    private readonly OlympContext _context = context;
    private readonly IAuthorizationService _authorizationService = authorizationService;

    public string Error { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var submission = await _context.Submissions
            .Include(x => x.CheckResults.OrderBy(x => x.CheckTime))
            .SingleOrDefaultAsync(x => x.Id == id);

        if (submission is null || submission.StatusCode != -1)
            return NotFound();

        var res = await _authorizationService.AuthorizeAsync(User, submission,
            new OperationAuthorizationRequirement { Name = OperationNames.ViewSolution });

        if (!res.Succeeded)
            return Forbid();

        var last = submission.CheckResults.Last();
        if (last.CheckTime < new DateTime(2023, 03, 01))
        {
            int index = last.Log.IndexOf("StandartOutput");
            Error = index != -1 ? last.Log[index..] : "Internal error!";
            return Page();
        }

        Error = last.Log;
        if (Error.StartsWith("Runner:", StringComparison.Ordinal) || Error.StartsWith("Compilation:", StringComparison.Ordinal))
        {
            var index = Error.IndexOf("Compilation:", StringComparison.Ordinal);
            // Skip runner metadata, the compilation heading and its two timing lines.
            Error = index >= 0 ? string.Join('\n', Error[index..].Split('\n').Skip(3)) : "Internal error!";
        }
        return Page();
    }
}

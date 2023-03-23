using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;

namespace Olymp.Site.Pages.Submissions;

public class ErrorsModel : PageModel
{
    private readonly OlympContext _context;
    private readonly IAuthorizationService _authorizationService;

    public string Error { get; private set; } = null!;

    public ErrorsModel(OlympContext context, IAuthorizationService authorizationService)
    {
        _context = context;
        _authorizationService = authorizationService;
    }

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
            Error = index != -1 ? last.Log.Substring(index) : "Internal error!";
            return Page();
        }

        Error = last.Log;
        return Page();
    }
}

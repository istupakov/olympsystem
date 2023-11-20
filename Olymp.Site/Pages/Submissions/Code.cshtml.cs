using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;

namespace Olymp.Site.Pages.Submissions;

public class CodeModel(OlympContext context, IAuthorizationService authorizationService) : PageModel
{
    private readonly OlympContext _context = context;
    private readonly IAuthorizationService _authorizationService = authorizationService;

    public string Code { get; private set; } = null!;
    public string Lang { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var data = await _context.Submissions
            .Where(x => x.Id == id)
            .Select(submission => new { submission, submission.Compilator.SourceExtension })
            .SingleOrDefaultAsync();

        if (data is null)
            return NotFound();

        (Code, Lang) = (data.submission.Text, data.SourceExtension[1..]);

        var res = await _authorizationService.AuthorizeAsync(User, data.submission,
            new OperationAuthorizationRequirement { Name = OperationNames.ViewSolution });

        return res.Succeeded ? Page() : Forbid();
    }
}

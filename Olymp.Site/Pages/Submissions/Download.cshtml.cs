using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;

namespace Olymp.Site.Pages.Submissions;

public class DownloadModel(OlympContext context, IAuthorizationService authorizationService) : PageModel
{
    private readonly OlympContext _context = context;
    private readonly IAuthorizationService _authorizationService = authorizationService;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var data = await _context.Submissions
            .Where(x => x.Id == id)
            .Select(submission => new { submission, submission.Compilator.SourceExtension })
            .SingleOrDefaultAsync();

        if (data is null)
            return NotFound();

        var res = await _authorizationService.AuthorizeAsync(User, data.submission,
            new OperationAuthorizationRequirement { Name = OperationNames.ViewSolution });

        if (!res.Succeeded)
            return Forbid();

        return File(data.submission.SourceCode, MediaTypeNames.Text.Plain, Path.ChangeExtension(data.submission.DescriptionOrId, data.SourceExtension));
    }
}

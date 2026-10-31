using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

using Olymp.Checker;
using Olymp.Site.Services.Checker;

namespace Olymp.Site.Pages.Checkers;

public class IndexModel(ICheckerManager checkerManager,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    [TempData]
    public string? StatusMessage { get; set; }

    public IEnumerable<ICheckerService> Checkers => checkerManager.Checkers.Values;

    public async Task<IActionResult> OnPostAddCompilatorsAsync(Guid checkerId, CancellationToken token)
    {
        if (!User.IsInRole(RoleNames.Admin))
            return Forbid();

        try
        {
            var count = await checkerManager.AddCompilators(checkerId, token);
            StatusMessage = localizer["Added or enabled compilators: {0}", count];
            return RedirectToPage();
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, localizer[ex.Message]);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDisableCompilatorsAsync(CancellationToken token)
    {
        if (!User.IsInRole(RoleNames.Admin))
            return Forbid();

        try
        {
            var count = await checkerManager.DisableCompilatorsWithoutRunner(token);
            StatusMessage = localizer["Disabled compilators: {0}", count];
            return RedirectToPage();
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, localizer[ex.Message]);
            return Page();
        }
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

using Olymp.Checker;
using Olymp.Site.Services.Checker;

namespace Olymp.Site.Pages.Checkers;

public class IndexModel(ICheckerManager checkerManager, IEnumerable<ICheckerTest> checkerTests,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    private readonly ICheckerManager _checkerManager = checkerManager;

    [TempData]
    public string? StatusMessage { get; set; }

    public IEnumerable<ICheckerService> Checkers => _checkerManager.Checkers.Values;

    public int GetTestCount(ICompilatorConfiguration configuration) => checkerTests.Count(x =>
        x.Language == configuration.Language
        && (x.CompilatorNames.Count == 0 || x.CompilatorNames.Contains(configuration.Name)));

    public async Task<IActionResult> OnPostAddCompilatorsAsync(Guid checkerId, CancellationToken token)
    {
        if (!User.IsInRole(RoleNames.Admin))
            return Forbid();

        try
        {
            var count = await _checkerManager.AddCompilators(checkerId, token);
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
            var count = await _checkerManager.DisableCompilatorsWithoutRunner(token);
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

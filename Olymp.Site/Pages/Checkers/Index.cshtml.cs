using Microsoft.AspNetCore.Mvc.RazorPages;

using Olymp.Site.Services.Checker;

namespace Olymp.Site.Pages.Checkers;

public class IndexModel(ICheckerManager checkerManager) : PageModel
{
    private readonly ICheckerManager _checkerManager = checkerManager;

    public IEnumerable<ICheckerService> Checkers => _checkerManager.Checkers.Values;

    public void OnGet()
    {

    }
}

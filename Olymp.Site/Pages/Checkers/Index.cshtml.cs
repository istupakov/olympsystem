using Microsoft.AspNetCore.Mvc.RazorPages;

using Olymp.Site.Services.Checker;

namespace Olymp.Site.Pages.Checkers;

public class IndexModel : PageModel
{
    private readonly ICheckerManager _checkerManager;

    public IEnumerable<ICheckerService> Checkers => _checkerManager.Checkers.Values;

    public IndexModel(ICheckerManager checkerManager)
    {
        _checkerManager = checkerManager;
    }

    public void OnGet()
    {

    }
}

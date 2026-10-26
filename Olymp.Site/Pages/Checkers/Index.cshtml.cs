using Microsoft.AspNetCore.Mvc.RazorPages;

using Olymp.Checker;
using Olymp.Site.Services.Checker;

namespace Olymp.Site.Pages.Checkers;

public class IndexModel(ICheckerManager checkerManager, IEnumerable<ICheckerTest> checkerTests) : PageModel
{
    private readonly ICheckerManager _checkerManager = checkerManager;

    public IEnumerable<ICheckerService> Checkers => _checkerManager.Checkers.Values;

    public int GetTestCount(ICompilatorConfiguration configuration) => checkerTests.Count(x =>
        x.Language == configuration.Language
        && (x.CompilatorNames.Count == 0 || x.CompilatorNames.Contains(configuration.Name)));
}

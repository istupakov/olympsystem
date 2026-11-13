using Olymp.Checker;
using Olymp.Domain.Models;

namespace Olymp.WebApp.Services.Checker;

public static class RunnerAvailability
{
    public static bool SupportsCompiler(this ICheckerService runner, Compilator compiler) =>
        runner.SelfTestingStatus == CheckerSelfTestingStatus.Success
        && (runner.Compilators?.Any(info => info.Configuration.Name == compiler.Name
            && info.DisplayName == compiler.Description && info.Error is null) ?? false);
}

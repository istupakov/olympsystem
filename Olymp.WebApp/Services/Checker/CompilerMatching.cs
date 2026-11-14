using Olymp.Checker;
using Olymp.Domain.Models;

namespace Olymp.WebApp.Services.Checker;

public static class CompilerMatching
{
    public static bool Matches(Compilator compiler, CompilatorInfo info) => CompilerConfigurationKey.From(compiler) == CompilerConfigurationKey.From(info);

    public static bool CanCheck(this ICheckerService runner, Compilator compiler) =>
        runner.SelfTestingStatus == CheckerSelfTestingStatus.Success
        && (runner.Compilators?.Any(info => info.Error is null && Matches(compiler, info)) ?? false);
}

public sealed record CompilerConfigurationKey(string Name, string? Description, string Language,
    string Environment, string SourceExtension, string CommandLine)
{
    public static CompilerConfigurationKey From(Compilator compiler) => new(compiler.Name, compiler.Description,
        compiler.Language, compiler.ConfigName, compiler.SourceExtension, compiler.CommandLine);

    public static CompilerConfigurationKey From(CompilatorInfo info) => new(info.Configuration.Name, info.DisplayName,
        info.Configuration.Language, info.Configuration.ConfigName, info.Configuration.SourceExtension, info.Configuration.CompileCommand);
}

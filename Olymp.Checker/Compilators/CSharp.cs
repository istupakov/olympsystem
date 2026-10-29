using System.Text.RegularExpressions;

namespace Olymp.Checker.Compilators;

public sealed partial class CSharpFrameworkConfiguration() : CompilatorConfiguration(
    "C#", "vs2026", ".cs", "csc /o /r:System.Numerics.dll /utf8output !.!")
{
    public override string Name => "C# .NET Framework";
    public override string GetVersionCommand() =>
        "csc -version && reg query \"HKLM\\SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full\" /v Version";

    public override CompilatorInfo ParseInfo(string output)
    {
        var compiler = ReadVersion(output, NumericVersionRegex);
        var framework = ReadVersion(output, FrameworkVersionRegex);
        return new(this, $"C# (.NET Framework{VersionSuffix(framework)})",
            $"Compiler {compiler}; .NET Framework {framework}");
    }

    [GeneratedRegex(@"^\s*Version\s+REG_SZ\s+(?<v>" + VersionNumberPattern + @")\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FrameworkVersionRegex { get; }
}

public sealed partial class CSharpDotnetConfiguration() : CompilatorConfiguration(
    "C#", "dotnet", ".cs", "dotnet-csc.cmd !.!")
{
    public override string Name => "C# .NET";
    public override string GetVersionCommand() =>
        "dotnet --version && dotnet --list-runtimes && dotnet-csc.cmd -out:version-probe.dll -langversion:?";
    public override string RunCommand(string name, int memoryMb) => $"dotnet {Path.ChangeExtension(name, ".dll")}";

    public override CompilatorInfo ParseInfo(string output)
    {
        var sdk = ReadVersion(output, NumericVersionRegex);
        // Match dotnet-csc.cmd, which selects the last Microsoft.NETCore.App runtime listed.
        var runtimes = RuntimeVersionRegex.Matches(output);
        var runtime = runtimes.Count > 0 ? runtimes[^1].Groups["v"].Value
            : throw new FormatException("Microsoft.NETCore.App runtime not found");
        var language = ReadVersion(output, LanguageVersionRegex);
        return new(this, $"C#{VersionSuffix(language)} (.NET{VersionSuffix(runtime)})",
            $"C# {language}; .NET {runtime}; SDK {sdk}");
    }

    [GeneratedRegex(@"^Microsoft\.NETCore\.App\s+(?<v>\S+)", RegexOptions.Multiline)]
    private static partial Regex RuntimeVersionRegex { get; }

    [GeneratedRegex(@"^\s*(?<v>\d+(?:\.\d+)*)\s+\(default\)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LanguageVersionRegex { get; }
}

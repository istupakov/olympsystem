using System.Text.RegularExpressions;

namespace Olymp.Checker.Compilators;

public abstract partial class MsvcConfiguration(
    string language, string displayLanguage, string sourceExtension, string compileCommand)
    : CompilatorConfiguration(language, "vs2026", sourceExtension, compileCommand)
{
    public override string Name => $"MSVC {displayLanguage}";
    public override string GetVersionCommand() => "cl 2>&1 & exit /b 0";
    public override CompilatorInfo ParseInfo(string output)
    {
        var version = ReadVersion(output, CompilerVersionRegex);
        return new(this, $"{displayLanguage} (MSVC 2026)", $"MSVC {version}; Visual Studio 2026");
    }

    [GeneratedRegex(@"^[^\r\n]*C/C\+\+[^\r\n]*?\b(?<v>" + VersionNumberPattern + @")\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CompilerVersionRegex { get; }
}

public sealed class MsvcCConfiguration() : MsvcConfiguration(
    "C", "C", ".c", "cl /O2 /TC /utf-8 !.!")
{ }

public sealed class MsvcCpp20Configuration() : MsvcConfiguration(
    "C++", "C++20", ".cpp", "cl /O2 /EHs /TP /std:c++20 /utf-8 !.!")
{ }

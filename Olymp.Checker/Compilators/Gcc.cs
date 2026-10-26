namespace Olymp.Checker.Compilators;

public abstract class GccConfiguration(
    string language, string displayLanguage, string sourceExtension, string executable, string compileCommand)
    : CompilatorConfiguration(language, "mingw", sourceExtension, compileCommand)
{
    public override string Name => $"GCC {displayLanguage}";
    public override string GetVersionCommand() => $"{executable} -dumpfullversion -dumpversion";
    protected override string GetDisplayName(string version) => $"{displayLanguage} (GCC{VersionSuffix(version)})";
    public override CompilatorInfo ParseInfo(string output)
    {
        var version = ReadVersion(output, NumericVersionRegex);
        return CreateInfo(version, $"GCC {version}");
    }
}

public sealed class GccCConfiguration() : GccConfiguration(
    "C", "C", ".c", "gcc", "gcc -O2 -Wl,--stack=67108864 -x c !.! -o!")
{ }

public sealed class GccCpp20Configuration() : GccConfiguration(
    "C++", "C++20", ".cpp", "g++", "g++ -O2 -Wl,--stack=67108864 -x c++ -std=c++20 !.! -o!")
{ }

public sealed class GccCpp23Configuration() : GccConfiguration(
    "C++", "C++23", ".cpp", "g++", "g++ -O2 -Wl,--stack=67108864 -x c++ -std=c++23 !.! -o!")
{ }

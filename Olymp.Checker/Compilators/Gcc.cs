namespace Olymp.Checker.Compilators;

public abstract class GccConfiguration(
    string language, string displayLanguage, string sourceExtension, string executable, string languageOptions,
    bool linux = false)
    : CompilatorConfiguration(language, linux ? "gcc" : "mingw", sourceExtension,
        $"{executable} -O2{(linux ? string.Empty : " -Wl,--stack=67108864")} {languageOptions} !.! -o!")
{
    public override string Name => $"GCC {displayLanguage}";
    public override string GetVersionCommand() => $"{executable} -dumpfullversion -dumpversion";
    public override string RunCommand(string name, int memoryMb) => linux
        ? $"./{Path.GetFileNameWithoutExtension(name)}" : base.RunCommand(name, memoryMb);

    public override CompilatorInfo ParseInfo(string output)
    {
        var version = ReadVersion(output, NumericVersionRegex);
        return new(this, $"{displayLanguage} (GCC{VersionSuffix(version)}{(linux ? ", Linux" : string.Empty)})", $"GCC {version}");
    }
}

public class GccCConfiguration : GccConfiguration
{
    public GccCConfiguration() : this(linux: false) { }

    protected GccCConfiguration(bool linux) : base("C", "C", ".c", "gcc", "-x c", linux) { }
}

public class GccCpp23Configuration : GccConfiguration
{
    public GccCpp23Configuration() : this(linux: false) { }

    protected GccCpp23Configuration(bool linux) : base("C++", "C++23", ".cpp", "g++", "-x c++ -std=c++23", linux) { }
}

public sealed class GccLinuxCConfiguration() : GccCConfiguration(linux: true)
{ }

public sealed class GccLinuxCpp23Configuration() : GccCpp23Configuration(linux: true)
{ }

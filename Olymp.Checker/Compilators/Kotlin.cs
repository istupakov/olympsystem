using System.Text.RegularExpressions;

namespace Olymp.Checker.Compilators;

public sealed partial class KotlinConfiguration() : CompilatorConfiguration(
    "Kotlin", "kotlin", ".kt", "kotlinc !.! -include-runtime -d !.jar")
{
    public override string Name => "Kotlin";
    public override bool UseSlowTimeLimit => true;
    public override int MemoryOverheadMb => 128;
    public override string GetVersionCommand() => "kotlinc -version && java -version";
    public override string RunCommand(string name, int memoryMb) =>
        $"java -Xmx{memoryMb}M -Xss64M -jar {Path.ChangeExtension(name, ".jar")}";

    public override CompilatorInfo ParseInfo(string output)
    {
        var kotlin = ReadVersion(output, CompilerVersionRegex);
        var jre = ReadVersion(output, RuntimeVersionRegex);
        return new(this, $"Kotlin{VersionSuffix(kotlin)} (OpenJDK{VersionSuffix(jre)})",
            $"Kotlin {kotlin}; OpenJDK {jre}");
    }

    [GeneratedRegex(@"\bkotlinc-jvm\s+(?<v>\S+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CompilerVersionRegex { get; }

    [GeneratedRegex(@"^(?:openjdk|java)(?: version)? ""(?<v>[^""]+)""", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeVersionRegex { get; }
}

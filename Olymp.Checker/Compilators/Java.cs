using System.Text.RegularExpressions;

namespace Olymp.Checker.Compilators;

public sealed partial class JavaConfiguration() : CompilatorConfiguration(
    "Java", "java", ".java", "javac Main.java")
{
    public override string Name => "Java";
    public override string GetSourceFileName(string name) => "Main.java";
    public override bool UseSlowTimeLimit => true;
    public override int MemoryOverheadMb => 128;
    public override string GetVersionCommand() => "javac -version && java -version";
    protected override string GetDisplayName(string version) => $"Java{VersionSuffix(version)} (OpenJDK)";
    public override string RunCommand(string name, int memoryMb) => $"java -Xmx{memoryMb}M -Xss64M Main";

    public override CompilatorInfo ParseInfo(string output)
    {
        var javac = ReadVersion(output, CompilerVersionRegex);
        var java = ReadVersion(output, RuntimeVersionRegex);
        return new(this, $"Java {javac.Split('.')[0]} (OpenJDK{VersionSuffix(java)})",
            $"javac {javac}; OpenJDK {java}");
    }

    [GeneratedRegex(@"^javac\s+(?<v>\S+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CompilerVersionRegex { get; }

    [GeneratedRegex(@"^(?:openjdk|java)(?: version)? ""(?<v>[^""]+)""", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeVersionRegex { get; }
}

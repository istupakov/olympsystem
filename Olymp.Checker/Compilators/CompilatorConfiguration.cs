using System.Text.RegularExpressions;

namespace Olymp.Checker.Compilators;

public abstract partial class CompilatorConfiguration(
    string language, string configName, string sourceExtension, string compileCommand) : ICompilatorConfiguration
{
    public abstract string Name { get; }
    public string Language { get; } = language;
    public string ConfigName { get; } = configName;
    public string SourceExtension { get; } = sourceExtension;
    public string CompileCommand { get; } = compileCommand;

    public virtual bool UseSlowTimeLimit => false;
    public virtual int MemoryOverheadMb => 0;

    public virtual string GetSourceFileName(string name) => Path.ChangeExtension(name, SourceExtension);

    public string GetCompileCommand(string name)
    {
        var filename = GetSourceFileName(name);
        return CompileCommand.Replace("!.!", filename).Replace("!", Path.GetFileNameWithoutExtension(filename));
    }

    public virtual string RunCommand(string name, int memoryMb) => Path.ChangeExtension(name, ".exe");

    public abstract string GetVersionCommand();
    public abstract CompilatorInfo ParseInfo(string output);

    protected const string VersionNumberPattern = @"\d+\.\d+(?:\.\d+)*(?:[-+][\w.-]+)?";

    [GeneratedRegex(@"^\s*(?<v>" + VersionNumberPattern + @")(?:[ \t]+\([0-9a-fA-F]+\))?\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    protected static partial Regex NumericVersionRegex { get; }

    protected string ReadVersion(string output, Regex regex)
    {
        var match = regex.Match(output);
        return match.Success ? match.Groups["v"].Value
            : throw new FormatException($"Cannot determine version for compilator {Name}");
    }

    protected static string VersionSuffix(string version)
    {
        var displayVersion = string.Join('.', version.Split('.').Take(2));
        if (displayVersion.EndsWith(".0", StringComparison.Ordinal))
            displayVersion = displayVersion[..^2];
        return $" {displayVersion}";
    }
}

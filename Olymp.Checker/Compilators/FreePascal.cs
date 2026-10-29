namespace Olymp.Checker.Compilators;

public sealed class FreePascalConfiguration() : CompilatorConfiguration(
    "Pascal", "pascal", ".pas", "fpc -Sd -Sc -Sg -Sh -O3 -FE. -FcUTF8 !.!")
{
    public override string Name => "Pascal";
    public override string GetVersionCommand() => "fpc -iV";
    protected override string GetDisplayName(string version) => $"Pascal (Free Pascal{VersionSuffix(version)})";
    public override CompilatorInfo ParseInfo(string output)
    {
        var version = ReadVersion(output, NumericVersionRegex);
        return CreateInfo(version, $"Free Pascal {version}");
    }
}

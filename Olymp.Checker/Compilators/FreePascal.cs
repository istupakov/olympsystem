namespace Olymp.Checker.Compilators;

public sealed class FreePascalConfiguration() : CompilatorConfiguration(
    "Pascal", "pascal", ".pas", "fpc -Sd -Sc -Sg -Sh -O3 -FE. -FcUTF8 !.!")
{
    public override string Name => "Pascal";
    public override string GetVersionCommand() => "fpc -iV";
    public override CompilatorInfo ParseInfo(string output)
    {
        var version = ReadVersion(output, NumericVersionRegex);
        return new(this, $"Pascal (Free Pascal{VersionSuffix(version)})", $"Free Pascal {version}");
    }
}

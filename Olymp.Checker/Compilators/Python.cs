using System.Text.RegularExpressions;

namespace Olymp.Checker.Compilators;

public sealed partial class CPythonConfiguration() : CompilatorConfiguration(
    "Python", "python", ".py", "python -m compileall -b !.!")
{
    public override string Name => "Python";
    public override bool UseSlowTimeLimit => true;
    public override string GetVersionCommand() => "python --version";
    public override string RunCommand(string name, int memoryMb) => $"python {Path.ChangeExtension(name, ".pyc")}";
    public override CompilatorInfo ParseInfo(string output)
    {
        var version = ReadVersion(output, PythonVersionRegex);
        return new(this, $"Python{VersionSuffix(version)} (CPython)", $"CPython {version}");
    }

    [GeneratedRegex(@"^Python\s+(?<v>" + VersionNumberPattern + @")\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PythonVersionRegex { get; }
}

public sealed partial class PyPyConfiguration() : CompilatorConfiguration(
    "Python", "pypy", ".py", "pypy -m compileall -b -q !.!")
{
    public override string Name => "PyPy";
    public override bool UseSlowTimeLimit => true;
    public override string GetVersionCommand() => "pypy --version";
    public override string RunCommand(string name, int memoryMb) => $"pypy {Path.ChangeExtension(name, ".pyc")}";

    public override CompilatorInfo ParseInfo(string output)
    {
        var python = ReadVersion(output, PythonVersionRegex);
        var pypy = ReadVersion(output, PyPyVersionRegex);
        return new(this, $"Python{VersionSuffix(python)} (PyPy{VersionSuffix(pypy)})",
            $"Python {python}; PyPy {pypy}");
    }

    [GeneratedRegex(@"^Python\s+(?<v>" + VersionNumberPattern + @")\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PythonVersionRegex { get; }

    [GeneratedRegex(@"\bPyPy\s+(?<v>" + VersionNumberPattern + @")\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PyPyVersionRegex { get; }
}

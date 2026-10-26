namespace Olymp.Checker;

public interface ICompilatorConfiguration
{
    string Name { get; }
    string Language { get; }
    string ConfigName { get; }
    string SourceExtension { get; }
    string CompileCommand { get; }
    bool UseSlowTimeLimit { get; }
    int MemoryOverheadMb { get; }

    string GetSourceFileName(string name);
    string GetCompileCommand(string name);
    string RunCommand(string name, int memoryMb);
    string GetVersionCommand();
    CompilatorInfo ParseInfo(string output);
}

public sealed record CompilatorInfo(ICompilatorConfiguration Configuration,
    string DisplayName, string? Description, string? Error = null);

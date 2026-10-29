namespace Olymp.Checker;

public interface IRunnerService
{
    Guid Id { get; }
    string Name { get; }
    DateTimeOffset ConnectingTime { get; }
    IReadOnlySet<string> SupportedEnvs { get; }

    Task<RunnerResult> Run(RunnerTask request, CancellationToken token = default);
}

public sealed record RunnerFile(string Filename, ReadOnlyMemory<byte> Content);

public sealed record RunnerResources(TimeSpan UserTime, TimeSpan TotalTime,
    ulong Memory, ulong Stdout, ulong Stderr);

public sealed record RunnerTask(string Command, ReadOnlyMemory<byte> Stdin,
    bool ClearWorkdir, bool ReadOnly,
    ICollection<string> EnvFiles, ICollection<RunnerFile> Files,
    RunnerResources ResourceLimits);

public sealed record RunnerResult(RunnerStatus Status, int ExitCode,
    ReadOnlyMemory<byte> Stdout, ReadOnlyMemory<byte> Stderr,
    RunnerResources ResourceConsumption);

public enum RunnerStatus
{
    Unspecified,
    Completed,
    Error,
    UserTimeLimit,
    TotalTimeLimit,
    MemoryLimit,
    StdoutLimit,
    StderrLimit
}

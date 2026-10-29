namespace Olymp.Checker;

public interface ICheckerService
{
    Guid Id { get; }
    string Name { get; }
    DateTimeOffset ConnectingTime { get; }
    IReadOnlyList<ICompilatorConfiguration> Configurations { get; }
    IReadOnlyList<CompilatorInfo>? Compilators { get; }
    CheckerSelfTestingStatus SelfTestingStatus { get; }
    IEnumerable<CheckerTestResult> SelfTestingResults { get; }

    Task<CheckerResult> Check(CheckRequest request, CancellationToken token);
    Task<string> Run(string source, ICompilatorConfiguration configuration, string input,
        TimeSpan timeLimit, int memoryLimitMb, CancellationToken token);
    Task<bool> RunSelfTests(CancellationToken token);
}

public sealed record CheckRequest(string Name, string Source,
    CompilatorInfo Compilator, int CheckerId, IReadOnlyList<CheckTest> Tests,
    TimeSpan TimeLimit, int MemoryLimitMb, bool AllTests);

public sealed record CheckTest(int Number, ReadOnlyMemory<byte> Input, ReadOnlyMemory<byte> Output, int? Score);

public sealed record CheckerResult(int StatusCode, string Log, int? Score = null, string? TestResults = null);

public enum CheckerSelfTestingStatus
{
    Unknown,
    InProcess,
    Failed,
    Success
}

public sealed record CheckerTestResult(string Name, CompilatorInfo Compilator,
    CheckerResultStatus ExpectedResult, CheckerResultStatus? Result, string Output);

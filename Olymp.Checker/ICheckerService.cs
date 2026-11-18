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
    Task<CheckerResult> Run(string source, ICompilatorConfiguration configuration, string input,
        TimeSpan timeLimit, int memoryLimitMb, CancellationToken token);
    Task<bool> RunSelfTests(CancellationToken token);
    Task<ValidatorResult> Validate(string source, CompilatorInfo compiler, IReadOnlyList<ValidatorTest> tests, CancellationToken token);
}

public sealed record CheckRequest(string Name, string Source,
    CompilatorInfo Compilator, int CheckerId, IReadOnlyList<CheckTest> Tests,
    TimeSpan TimeLimit, int MemoryLimitMb, bool AllTests);

public sealed record CheckTest(int Number, ReadOnlyMemory<byte> Input, ReadOnlyMemory<byte> Output, int? Score);

public sealed record CheckerResult(int StatusCode, string Log, int? Score = null, string? TestResults = null,
    TimeSpan? MaxUserTime = null, TimeSpan? MaxTotalTime = null, ulong? MaxMemory = null, int? SlowestTest = null);

public sealed record ValidatorTest(int Number, ReadOnlyMemory<byte> Input);
public sealed record ValidatorTestResult(int Number, bool IsValid);
public sealed record ValidatorResult(bool Compiled, string Log, IReadOnlyList<ValidatorTestResult> Tests);

public enum CheckerSelfTestingStatus
{
    Unknown,
    InProcess,
    Failed,
    Success
}

public sealed record CheckerTestResult(string Name, CompilatorInfo Compilator,
    CheckerResultStatus ExpectedResult, CheckerResultStatus? Result, string Output);

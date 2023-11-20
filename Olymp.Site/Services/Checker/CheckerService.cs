using System.Diagnostics;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Olymp.Checker;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site.Protos;

namespace Olymp.Site.Services.Checker;

public interface ICheckerService
{
    Guid Id { get; }
    string Name { get; }
    DateTimeOffset ConnectingTime { get; }
    IReadOnlySet<string> SupportedEnvs { get; }
    CheckerSelfTestingStatus SelfTestingStatus { get; }
    IEnumerable<CheckerTestResult> SelfTestingResults { get; }

    Task<bool> TryCheckFromDB(CancellationToken token);
    Task<CheckResult> Check(Submission submission, bool allTests, CancellationToken token);
    Task<string> Run(string source, Compilator compilator, string input,
        TimeSpan timeLimit, int memoryLimitMb, CancellationToken token);
    Task<bool> RunSelfTests(CancellationToken token);
}

public enum CheckerSelfTestingStatus
{
    Unknown,
    InProcess,
    Failed,
    Success
}

public record CheckerTestResult(ICheckerTest Test, Compilator Compilator, CheckerResultStatus Result, string Output);

public class CheckerService(ILogger<CheckerService> logger, IRunnerService runner, IServiceProvider provider,
    IEnumerable<ICheckerTest> checkerTests, IEnumerable<ISimpleChecker> simpleCheckers) : ICheckerService
{
    private readonly ILogger _logger = logger;
    private readonly IRunnerService _runner = runner;
    private readonly IServiceProvider _provider = provider;
    private readonly IEnumerable<ICheckerTest> _checkerTests = checkerTests;
    private readonly List<CheckerTestResult> _checkerTestResults = [];
    private readonly Dictionary<int, ISimpleChecker> _simpleCheckers = simpleCheckers.ToDictionary(x => x.Id);
    private readonly SemaphoreSlim _semaphore = new(1);

    public Guid Id => _runner.Id;
    public string Name => _runner.Name;
    public DateTimeOffset ConnectingTime => _runner.ConnectingTime;
    public IReadOnlySet<string> SupportedEnvs => _runner.SupportedEnvs;
    public CheckerSelfTestingStatus SelfTestingStatus { get; private set; } = CheckerSelfTestingStatus.Unknown;
    public IEnumerable<CheckerTestResult> SelfTestingResults => _checkerTestResults.AsReadOnly();

    private async Task<RunnerResult> Compile(string name, Compilator compilator,
        ReadOnlyMemory<byte> source, bool clearWorkdir, CancellationToken token)
    {
        if (!_runner.SupportedEnvs.Contains(compilator.ConfigName))
            throw new NotSupportedException($"Runner {_runner.Name} don't support config {compilator.ConfigName}");

        var filename = (compilator.Language is "Java") ? "Main.java"
            : Path.ChangeExtension(name, compilator.SourceExtension);

        var command = compilator.CommandLine.Replace("!.!", filename).Replace("!", Path.GetFileNameWithoutExtension(filename));

        var resourceLimits = new RunnerResources(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10),
            512 * 1024 * 1024, 0, 100 * 1024);
        return await _runner.Run(new($"{command} 1>&2", Memory<byte>.Empty, clearWorkdir, false,
            new[] { compilator.ConfigName }, new RunnerFile[] { new(filename, source) },
            resourceLimits), token);
    }

    private async Task<RunnerResult> RunTest(string name, Compilator compilator,
        ReadOnlyMemory<byte> input, TimeSpan timeLimit, int memoryLimitMb,
        CancellationToken token)
    {
        if (!_runner.SupportedEnvs.Contains(compilator.ConfigName))
            throw new NotSupportedException($"Runner {_runner.Name} don't support config {compilator.ConfigName}");

        var command = compilator.Language switch
        {
            "Java" => $"java -Xmx{memoryLimitMb}M -Xss64M Main",
            "Python" => Path.ChangeExtension(name, ".pyc"),
            "C#" when compilator.ConfigName.StartsWith("dotnet") => $"dotnet {Path.ChangeExtension(name, ".dll")}",
            _ => Path.ChangeExtension(name, ".exe")
        };

        if (compilator.Language == "Java")
            memoryLimitMb += 128;

        var resourceLimits = new RunnerResources(timeLimit, timeLimit + TimeSpan.FromSeconds(5),
            (uint)memoryLimitMb * 1024 * 1024, 1024 * 1024, 4096);
        return await _runner.Run(new(command, input, false, true, new[] { compilator.ConfigName }, [], resourceLimits), token);
    }

    private async Task<(CheckerResultStatus, RunnerResult)> CheckTest(string name,
        Compilator compilator, ISimpleChecker checker, ReadOnlyMemory<byte> input,
        ReadOnlyMemory<byte> output, TimeSpan timeLimit, int memoryLimitMb,
        CancellationToken token)
    {
        var encoding = Encoding.ASCII;
        var result = await RunTest(name, compilator, input, timeLimit, memoryLimitMb, token);
        var status = result.Status switch
        {
            CommandStatus.UserTimeLimit => CheckerResultStatus.TimeLimit,
            CommandStatus.TotalTimeLimit => CheckerResultStatus.IdlenessLimit,
            CommandStatus.MemoryLimit => CheckerResultStatus.MemoryLimit,
            CommandStatus.StdoutLimit => CheckerResultStatus.PresentationError,
            CommandStatus.StderrLimit => CheckerResultStatus.PresentationError,
            CommandStatus.Error => CheckerResultStatus.RuntimeError,
            CommandStatus.Completed => checker.Check(encoding.GetString(input.Span),
                encoding.GetString(output.Span), encoding.GetString(result.Stdout.Span)),
            _ => throw new Exception($"Runner unexpected result {result.Status}")
        };
        return (status, result);
    }

    public async Task<bool> TryCheckFromDB(CancellationToken token)
    {
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();

        var compilatorsId = await context.Compilators
                           .Where(x => x.IsActive && SupportedEnvs.Contains(x.ConfigName))
                           .Select(x => x.Id)
                           .ToListAsync(token);

        var span = DateTimeOffset.Now - TimeSpan.FromMinutes(1);
        var submission = await context.Submissions
            .Where(x => x.StatusCode == 0 || x.StatusCode == 1 && x.LastModification < span)
            .Where(x => x.Problem.IsActive && compilatorsId.Contains(x.CompilatorId))
            .OrderBy(x => x.CommitTime)
            .Include(x => x.Compilator)
            .Include(x => x.User)
            .Include(x => x.Problem)
            .ThenInclude(x => x.Tests.Where(x => x.IsActive).OrderBy(x => x.Number))
            .Include(x => x.Problem)
            .ThenInclude(x => x.Contest)
            .AsSplitQuery()
            .FirstOrDefaultAsync(token);

        if (submission is null)
            return false;

        _logger.LogDebug("Check: submission {id}, problem {problem}, compilator {compilator}, user {user}",
                submission.Id, submission.Problem.Name, submission.Compilator.Name, submission.User.Name);
        try
        {
            context.Attach(submission);
            submission.StatusCode = 1;
            submission.LastModification = DateTimeOffset.Now;
            await context.SaveChangesAsync(CancellationToken.None);

            var isSchool = submission.Problem.Contest.IsSchool;
            var log = await Check(submission, isSchool, token);

            context.CheckResults.Add(log);
            submission.StatusCode = log.StatusCode;
            submission.Score = log.Score;
            submission.ScoresByTest = log.TestResults;

            _logger.LogDebug("CheckLog for submission {id}: status: {status}, score {score}",
                submission.Id, log.StatusCode, log.Score);

            return true;
        }
        catch
        {
            submission.StatusCode = 0;
            throw;
        }
        finally
        {
            submission.LastModification = DateTimeOffset.Now;
            await context.SaveChangesAsync(CancellationToken.None);
        }
    }

    public async Task<CheckResult> Check(Submission submission, bool allTests, CancellationToken token)
    {
        await _semaphore.WaitAsync(token);
        var timer = Stopwatch.StartNew();

        try
        {
            var name = submission.Id.ToString();
            var compileResult = await Compile(name, submission.Compilator, submission.SourceCode, true, token);
            if (compileResult.Status is not CommandStatus.Completed)
            {
                return new()
                {
                    Submission = null!,
                    SubmissionId = submission.Id,
                    Log = GetTruncatedString(compileResult.Stderr, 1024),
                    StatusCode = CheckerResultStatus.CompilationError.MakeStatusCode(),
                    CheckTime = DateTimeOffset.Now
                };
            }

            if (!_simpleCheckers.TryGetValue(submission.Problem.CheckerId, out var checker))
                throw new InvalidOperationException($"Checker with id {submission.Problem.CheckerId} not found");

            var timeLimit = submission.Compilator.Language switch
            {
                "Java" or "Python" => TimeSpan.FromSeconds(submission.Problem.SlowTimeLimit),
                _ => TimeSpan.FromSeconds(submission.Problem.TimeLimit)
            };

            var log = new StringBuilder();
            var testResults = new List<CheckerResultStatus>();
            var statusCode = 2;
            foreach (var test in submission.Problem.Tests)
            {
                int counter = 0;
                log.AppendLine($"""
                    --------------
                    Test {test.Number}
                    """);
            test:
                var (status, result) = await CheckTest(name, submission.Compilator, checker,
                    test.Input, test.Output, timeLimit, submission.Problem.MemoryLimit, token);

                log.AppendLine($"""
                    status: {status}
                    user time: {result.ResourceConsumption.UserTime}
                    total time: {result.ResourceConsumption.TotalTime}
                    peak memory: {result.ResourceConsumption.Memory / 1024 / 1024} MB
                    exit code: {result.ExitCode}
                    """);

                if (status is CheckerResultStatus.TimeLimit or CheckerResultStatus.MemoryLimit && ++counter < 3)
                    goto test;

                testResults.Add(status);
                if (status is not CheckerResultStatus.Accepted)
                {
                    if (statusCode == 2)
                        statusCode = status.MakeStatusCode(test.Number);

                    log.AppendLine($"""
                        stdout:
                        {GetTruncatedString(result.Stdout, 1024)}
                        stderr:
                        {GetTruncatedString(result.Stderr, 1024)}
                        """);

                    if (!allTests)
                        break;
                }
            }

            log.AppendLine($"""
                --------------
                Checking time {timer.Elapsed}
                """);

            var needScores = allTests && submission.Problem.Tests.Any(x => x.Score is not null);
            return new()
            {
                Submission = null!,
                SubmissionId = submission.Id,
                Log = log.ToString(),
                StatusCode = statusCode,
                CheckTime = DateTimeOffset.Now,
                Score = !needScores ? null : submission.Problem.Tests
                    .Zip(testResults, (test, res) => res is CheckerResultStatus.Accepted ? test.Score : 0).Sum(),
                TestResults = !needScores ? null : string.Join(';', submission.Problem.Tests
                    .Zip(testResults, (test, res) => $"{(res is CheckerResultStatus.Accepted ? test.Score ?? 0 : res)}"))
            };
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<string> Run(string source, Compilator compilator,
        string input, TimeSpan timeLimit, int memoryLimitMb, CancellationToken token)
    {
        await _semaphore.WaitAsync(token);
        try
        {
            string name = Guid.NewGuid().ToString();
            var encoding = Encoding.ASCII;
            var compileResult = await Compile(name, compilator, encoding.GetBytes(source), true, token);
            if (compileResult.Status is not CommandStatus.Completed)
            {
                return $"""
                    status: {compileResult.Status}
                    user time: {compileResult.ResourceConsumption.UserTime}
                    total time: {compileResult.ResourceConsumption.TotalTime}
                    peak memory: {compileResult.ResourceConsumption.Memory / 1024 / 1024} MB
                    exit code: {compileResult.ExitCode}
                    compilator:
                    {encoding.GetString(compileResult.Stderr.Span)}
                    """;
            }

            var result = await RunTest(name, compilator, encoding.GetBytes(input),
                timeLimit, memoryLimitMb, token);

            return $"""
                status: {result.Status}
                user time: {result.ResourceConsumption.UserTime}
                total time: {result.ResourceConsumption.TotalTime}
                peak memory: {result.ResourceConsumption.Memory / 1024 / 1024} MB
                exit code: {result.ExitCode}
                compilator:
                {encoding.GetString(compileResult.Stderr.Span)}
                stdout:
                {GetTruncatedString(result.Stdout, 4096)}
                stderr:
                {GetTruncatedString(result.Stderr, 4096)}
                """;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool> RunSelfTests(CancellationToken token)
    {
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var compilators = await context.Compilators
            .Where(x => x.IsActive && SupportedEnvs.Contains(x.ConfigName))
            .ToListAsync(token);

        _checkerTestResults.Clear();
        SelfTestingStatus = CheckerSelfTestingStatus.InProcess;

        foreach (var compilator in compilators)
            foreach (var test in _checkerTests.Where(x => x.Language == compilator.Language))
            {
                token.ThrowIfCancellationRequested();
                await _semaphore.WaitAsync(token);
                try
                {
                    var (status, output) = await RunCheckerTest(test, compilator, token);
                    _checkerTestResults.Add(new(test, compilator, status, output));

                    if (status == test.ExpectedResult)
                        _logger.LogInformation("CheckerTest {name} with compilator {compilator}: OK",
                            test.Name, compilator.Name);
                    else
                        _logger.LogWarning("CheckerTest {name} with compilator {compilator} error: expected {expected}, actual {actual}",
                            test.Name, compilator.Name, test.ExpectedResult, status);
                }
                finally
                {
                    _semaphore.Release();
                }
            }

        if (_checkerTestResults.Where(x => !x.Test.Optional).All(x => x.Result == x.Test.ExpectedResult))
        {
            SelfTestingStatus = CheckerSelfTestingStatus.Success;
            return true;
        }

        SelfTestingStatus = CheckerSelfTestingStatus.Failed;
        return false;
    }

    private async Task<(CheckerResultStatus, string)> RunCheckerTest(ICheckerTest test,
        Compilator compilator, CancellationToken token)
    {
        var encoding = Encoding.ASCII;
        var compileResult = await Compile(test.Name, compilator, encoding.GetBytes(test.Source), true, token);
        if (compileResult.Status is not CommandStatus.Completed)
        {
            var compileOutput = $"""
                status: {compileResult.Status}
                user time: {compileResult.ResourceConsumption.UserTime}
                total time: {compileResult.ResourceConsumption.TotalTime}
                peak memory: {compileResult.ResourceConsumption.Memory / 1024 / 1024} MB
                exit code: {compileResult.ExitCode}
                source:
                {test.Source}
                compilator:
                {encoding.GetString(compileResult.Stderr.Span)}
                """;
            return (CheckerResultStatus.CompilationError, compileOutput);
        }

        var (status, result) = await CheckTest(test.Name, compilator, test.Checker,
        encoding.GetBytes(test.Input), encoding.GetBytes(test.Output),
        test.TimeLimit, test.MemoryLimitMB, token);

        var output = $"""
            status: {result.Status}
            user time: {result.ResourceConsumption.UserTime}
            total time: {result.ResourceConsumption.TotalTime}
            peak memory: {result.ResourceConsumption.Memory / 1024 / 1024} MB
            exit code: {result.ExitCode}
            source:
            {test.Source}
            compilator:
            {encoding.GetString(compileResult.Stderr.Span)}
            test input:
            {test.Input}
            test output
            {test.Output}
            stdout:
            {GetTruncatedString(result.Stdout, 1024)}
            stderr:
            {GetTruncatedString(result.Stderr, 1024)}
            """;

        return (status, output);
    }

    private static string GetTruncatedString(ReadOnlyMemory<byte> memory, int len) =>
        Encoding.UTF8.GetString(memory.Span[..Math.Min(memory.Length, len)]);
}

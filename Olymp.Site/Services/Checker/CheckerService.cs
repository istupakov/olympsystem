using System.Collections.Concurrent;
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
    IReadOnlyList<CompilatorInfo>? Compilators { get; }
    string VersionLog { get; }
    CheckerSelfTestingStatus SelfTestingStatus { get; }
    IEnumerable<CheckerTestResult> SelfTestingResults { get; }

    Task<bool> TryCheckFromDB(CancellationToken token);
    Task<CheckResult> Check(Submission submission, bool allTests, CancellationToken token);
    Task<string> Run(string source, ICompilatorConfiguration configuration, string input,
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

public record CheckerTestResult(ICheckerTest Test, CompilatorInfo Compilator, CheckerResultStatus Result, string Output);

public class CheckerService(ILogger<CheckerService> logger, IRunnerService runner, IServiceProvider provider,
    IEnumerable<ICompilatorConfiguration> compilatorConfigurations,
    IEnumerable<ICheckerTest> checkerTests, IEnumerable<ISimpleChecker> simpleCheckers) : ICheckerService
{
    private static readonly RunnerResources CompilationResources = new(
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
        768 * 1024 * 1024, 64 * 1024, 64 * 1024);

    private readonly ILogger _logger = logger;
    private readonly IRunnerService _runner = runner;
    private readonly IServiceProvider _provider = provider;
    private readonly IReadOnlyList<ICompilatorConfiguration> _configurations = [.. compilatorConfigurations];
    private readonly IEnumerable<ICheckerTest> _checkerTests = checkerTests;
    private readonly ConcurrentQueue<CheckerTestResult> _checkerTestResults = new();
    private readonly Dictionary<int, ISimpleChecker> _simpleCheckers = simpleCheckers.ToDictionary(x => x.Id);
    private readonly SemaphoreSlim _semaphore = new(1);

    public Guid Id => _runner.Id;
    public string Name => _runner.Name;
    public DateTimeOffset ConnectingTime => _runner.ConnectingTime;
    public IReadOnlySet<string> SupportedEnvs => _runner.SupportedEnvs;
    public IReadOnlyList<CompilatorInfo>? Compilators { get; private set; }
    public string VersionLog { get; private set; } = string.Empty;
    public CheckerSelfTestingStatus SelfTestingStatus { get; private set; } = CheckerSelfTestingStatus.Unknown;
    public IEnumerable<CheckerTestResult> SelfTestingResults => [.. _checkerTestResults];

    public async Task LoadCompilators(CancellationToken token)
    {
        if (Compilators is not null)
            return;

        await _semaphore.WaitAsync(token);
        try
        {
            if (Compilators is not null)
                return;

            var compilators = new List<CompilatorInfo>();
            var results = new Dictionary<(string Env, string Command), RunnerResult>();
            VersionLog = string.Empty;
            foreach (var configuration in _configurations.Where(x => SupportedEnvs.Contains(x.ConfigName)))
            {
                token.ThrowIfCancellationRequested();
                var command = configuration.GetVersionCommand();
                var output = string.Empty;
                try
                {
                    var key = (configuration.ConfigName, command);
                    if (!results.TryGetValue(key, out var result))
                    {
                        result = await _runner.Run(new(command, ReadOnlyMemory<byte>.Empty, false, false,
                            [configuration.ConfigName], [], CompilationResources), token);
                        results.Add(key, result);
                        VersionLog += $"Environment: {configuration.ConfigName}\n{GetCommandLog(command, result)}\n\n";
                    }

                    output = (Encoding.UTF8.GetString(result.Stdout.Span) + "\n"
                        + Encoding.UTF8.GetString(result.Stderr.Span)).Trim();
                    if (result.Status != CommandStatus.Completed || result.ExitCode != 0)
                        throw new InvalidOperationException($"status: {result.Status}, exit code: {result.ExitCode}");

                    compilators.Add(configuration.ParseInfo(output));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    compilators.Add(new(configuration, configuration.Name, null,
                        $"{command}\n{output}\n{ex.Message}"));
                }
            }

            Compilators = [.. compilators.OrderBy(x => x.DisplayName, StringComparer.Ordinal)];
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private CompilatorInfo GetCompilatorInfo(Compilator compilator)
    {
        var info = Compilators!.SingleOrDefault(x => x.Configuration.Name == compilator.Name
            && x.DisplayName == compilator.Description)
            ?? throw new NotSupportedException($"Compilator {compilator.Id} ({compilator.Name}) has no matching configuration on runner {Id}");

        if (info.Error is not null)
            throw new NotSupportedException($"Compilator {info.Configuration.Name} is unavailable:\n{info.Error}");

        return info;
    }

    private async Task<RunnerResult> Compile(string name, ICompilatorConfiguration configuration,
        ReadOnlyMemory<byte> source, bool clearWorkdir, CancellationToken token)
    {
        if (!_runner.SupportedEnvs.Contains(configuration.ConfigName))
            throw new NotSupportedException($"Runner {_runner.Name} don't support config {configuration.ConfigName}");

        var filename = configuration.GetSourceFileName(name);
        var command = configuration.GetCompileCommand(name);

        return await _runner.Run(new($"{command} 1>&2", Memory<byte>.Empty, clearWorkdir, false,
            [configuration.ConfigName], [new(filename, source)], CompilationResources), token);
    }

    private async Task<RunnerResult> RunTest(string name, ICompilatorConfiguration configuration,
        ReadOnlyMemory<byte> input, TimeSpan timeLimit, int memoryLimitMb,
        CancellationToken token)
    {
        if (!_runner.SupportedEnvs.Contains(configuration.ConfigName))
            throw new NotSupportedException($"Runner {_runner.Name} don't support config {configuration.ConfigName}");

        var command = configuration.RunCommand(name, memoryLimitMb);
        memoryLimitMb += configuration.MemoryOverheadMb;

        var resourceLimits = new RunnerResources(timeLimit, timeLimit + TimeSpan.FromSeconds(5),
            (uint)memoryLimitMb * 1024 * 1024, 1024 * 1024, 4096);
        return await _runner.Run(new(command, input, false, true, [configuration.ConfigName], [], resourceLimits), token);
    }

    private async Task<(CheckerResultStatus, RunnerResult)> CheckTest(string name,
        ICompilatorConfiguration configuration, ISimpleChecker checker, ReadOnlyMemory<byte> input,
        ReadOnlyMemory<byte> output, TimeSpan timeLimit, int memoryLimitMb,
        CancellationToken token)
    {
        var encoding = Encoding.UTF8;
        var result = await RunTest(name, configuration, input, timeLimit, memoryLimitMb, token);
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

        var available = Compilators!.Where(x => x.Error is null)
            .Select(x => (x.Configuration.Name, x.DisplayName)).ToHashSet();
        var records = await context.Compilators.Where(x => x.IsActive)
            .Select(x => new { x.Id, x.Name, x.Description }).ToListAsync(token);
        var compilatorIds = records
            .Where(x => x.Description is not null && available.Contains((x.Name, x.Description)))
            .Select(x => x.Id).ToArray();
        if (compilatorIds.Length == 0)
            return false;

        var span = DateTimeOffset.Now - TimeSpan.FromMinutes(2);
        var submission = await context.Submissions
            .Where(x => x.StatusCode == 0 || (x.StatusCode == 1 && x.LastModification < span))
            .Where(x => x.Problem.IsActive && compilatorIds.Contains(x.CompilatorId))
            .Where(x => x.Problem.Tests.Any(test => test.IsActive))
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
        var now = DateTimeOffset.Now;
        var updated = await context.Submissions
            .Where(x => x.Id == submission.Id
                && (x.StatusCode == 0 || (x.StatusCode == 1 && x.LastModification < span)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.StatusCode, 1)
                .SetProperty(x => x.LastModification, now), token);
        if (updated == 0)
            return true;

        submission.StatusCode = 1;
        submission.LastModification = now;
        context.Attach(submission);

        try
        {
            _logger.LogDebug("Check: submission {id}, problem {problem}, compilator {compilator}, user {user}",
                submission.Id, submission.Problem.Name, submission.Compilator.Name, submission.User.Name);
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
        await LoadCompilators(token);
        await _semaphore.WaitAsync(token);
        var timer = Stopwatch.StartNew();

        try
        {
            var name = submission.Id.ToString();
            var compilator = GetCompilatorInfo(submission.Compilator);
            var configuration = compilator.Configuration;
            var log = new StringBuilder();
            log.AppendLine($"""
                Runner: {Name} ({Id})
                Compilator: {compilator.DisplayName}
                Configuration: {configuration.Name} ({configuration.ConfigName})
                Versions: {compilator.Description}
                """);

            var compileResult = await Compile(name, configuration, submission.SourceCode, true, token);
            log.AppendLine($"""
                Compilation:
                user time: {compileResult.ResourceConsumption.UserTime}
                total time: {compileResult.ResourceConsumption.TotalTime}
                """);
            if (compileResult.Status is not CommandStatus.Completed)
            {
                return new()
                {
                    Submission = null!,
                    SubmissionId = submission.Id,
                    Log = log.AppendLine(GetTruncatedString(compileResult.Stderr, 1024)).ToString(),
                    StatusCode = CheckerResultStatus.CompilationError.MakeStatusCode(),
                    CheckTime = DateTimeOffset.Now
                };
            }

            if (!_simpleCheckers.TryGetValue(submission.Problem.CheckerId, out var checker))
                throw new InvalidOperationException($"Checker with id {submission.Problem.CheckerId} not found");

            var timeLimit = TimeSpan.FromSeconds(configuration.UseSlowTimeLimit
                ? submission.Problem.SlowTimeLimit : submission.Problem.TimeLimit);

            var testResults = new List<CheckerResultStatus>();
            var statusCode = 2;
            var maxUserTime = TimeSpan.Zero;
            var maxTotalTime = TimeSpan.Zero;
            var maxPeakMemory = 0ul;
            foreach (var test in submission.Problem.Tests)
            {
                int counter = 0;
                log.AppendLine($"""
                    --------------
                    Test {test.Number}
                    """);
            test:
                var (status, result) = await CheckTest(name, configuration, checker,
                    test.Input, test.Output, timeLimit, submission.Problem.MemoryLimit, token);

                if (result.ResourceConsumption.UserTime > maxUserTime)
                    maxUserTime = result.ResourceConsumption.UserTime;

                if (result.ResourceConsumption.TotalTime > maxTotalTime)
                    maxTotalTime = result.ResourceConsumption.TotalTime;

                maxPeakMemory = Math.Max(maxPeakMemory, result.ResourceConsumption.Memory);

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
                Max user time: {maxUserTime}
                Max total time: {maxTotalTime}
                Max peak memory: {maxPeakMemory / 1024 / 1024} MB
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

    public async Task<string> Run(string source, ICompilatorConfiguration configuration,
        string input, TimeSpan timeLimit, int memoryLimitMb, CancellationToken token)
    {
        await _semaphore.WaitAsync(token);
        try
        {
            string name = Guid.NewGuid().ToString();
            var encoding = Encoding.UTF8;
            var compileResult = await Compile(name, configuration, encoding.GetBytes(source), true, token);
            var compilationLog = GetCompilationLog(configuration.GetCompileCommand(name), compileResult);
            if (compileResult.Status is not CommandStatus.Completed)
                return compilationLog;

            var result = await RunTest(name, configuration, encoding.GetBytes(input),
                timeLimit, memoryLimitMb, token);

            return $"""
                {compilationLog}

                Execution:
                status: {result.Status}
                user time: {result.ResourceConsumption.UserTime}
                total time: {result.ResourceConsumption.TotalTime}
                peak memory: {result.ResourceConsumption.Memory / 1024 / 1024} MB
                exit code: {result.ExitCode}
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
        await LoadCompilators(token);
        var compilators = Compilators!;

        _checkerTestResults.Clear();
        SelfTestingStatus = CheckerSelfTestingStatus.InProcess;

        if (compilators.Count == 0)
        {
            _logger.LogWarning("Checker {id} has no supported compilators to test", Id);
            SelfTestingStatus = CheckerSelfTestingStatus.Failed;
            return false;
        }

        var hasTestsForAllCompilators = true;
        foreach (var compilator in compilators)
        {
            if (compilator.Error is not null)
                continue;

            var tests = _checkerTests.Where(x => x.Language == compilator.Configuration.Language
                && (x.CompilatorNames.Count == 0 || x.CompilatorNames.Contains(compilator.Configuration.Name))).ToArray();
            if (tests.Length == 0)
            {
                _logger.LogWarning("No self-tests for compilator {compilator}, language {language}",
                    compilator.Configuration.Name, compilator.Configuration.Language);
                hasTestsForAllCompilators = false;
            }

            foreach (var test in tests)
            {
                token.ThrowIfCancellationRequested();
                await _semaphore.WaitAsync(token);
                try
                {
                    var (status, output) = await RunCheckerTest(test, compilator.Configuration, token);
                    _checkerTestResults.Enqueue(new(test, compilator, status, output));

                    if (status == test.ExpectedResult)
                        _logger.LogInformation("CheckerTest {name} with compilator {compilator}: OK",
                            test.Name, compilator.DisplayName);
                    else
                        _logger.LogWarning("CheckerTest {name} with compilator {compilator} error: expected {expected}, actual {actual}",
                            test.Name, compilator.DisplayName, test.ExpectedResult, status);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "CheckerTest {name} with compilator {compilator} failed", test.Name, compilator.Configuration.Name);
                    _checkerTestResults.Enqueue(new(test, compilator, CheckerResultStatus.InternalError, ex.ToString()));
                }
                finally
                {
                    _semaphore.Release();
                }
            }
        }

        if (compilators.All(x => x.Error is null) && hasTestsForAllCompilators
            && _checkerTestResults.All(x => x.Result == x.Test.ExpectedResult))
        {
            SelfTestingStatus = CheckerSelfTestingStatus.Success;
            return true;
        }

        SelfTestingStatus = CheckerSelfTestingStatus.Failed;
        return false;
    }

    private async Task<(CheckerResultStatus, string)> RunCheckerTest(ICheckerTest test,
        ICompilatorConfiguration configuration, CancellationToken token)
    {
        var encoding = Encoding.UTF8;
        var compileResult = await Compile(test.Name, configuration, encoding.GetBytes(test.Source), true, token);
        var compilationLog = GetCompilationLog(configuration.GetCompileCommand(test.Name), compileResult);
        if (compileResult.Status is not CommandStatus.Completed)
        {
            return (CheckerResultStatus.CompilationError, $"""
                {compilationLog}
                source:
                {test.Source}
                """);
        }

        var (status, result) = await CheckTest(test.Name, configuration, test.Checker,
        encoding.GetBytes(test.Input), encoding.GetBytes(test.Output),
        test.TimeLimit, test.MemoryLimitMB, token);

        var output = $"""
            {compilationLog}

            Execution:
            status: {result.Status}
            user time: {result.ResourceConsumption.UserTime}
            total time: {result.ResourceConsumption.TotalTime}
            peak memory: {result.ResourceConsumption.Memory / 1024 / 1024} MB
            exit code: {result.ExitCode}
            source:
            {test.Source}
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

    private static string GetCommandLog(string command, RunnerResult result) => $"""
        command: {command}
        status: {result.Status}
        user time: {result.ResourceConsumption.UserTime}
        total time: {result.ResourceConsumption.TotalTime}
        peak memory: {result.ResourceConsumption.Memory / 1024 / 1024} MB
        exit code: {result.ExitCode}
        """;

    private static string GetCompilationLog(string command, RunnerResult result) => $"""
        Compilation:
        {GetCommandLog(command, result)}
        compilator:
        {GetTruncatedString(result.Stderr, 100 * 1024)}
        """;

    private static string GetTruncatedString(ReadOnlyMemory<byte> memory, int len) =>
        Encoding.UTF8.GetString(memory.Span[..Math.Min(memory.Length, len)]);
}

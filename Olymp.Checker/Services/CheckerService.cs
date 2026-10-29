using System.Diagnostics;
using System.Text;

namespace Olymp.Checker.Services;

public sealed class CheckerService : ICheckerService
{
    private static readonly RunnerResources CompilationResources = new(
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
        768 * 1024 * 1024, 64 * 1024, 64 * 1024);

    private readonly IRunnerService _runner;
    private readonly IReadOnlyList<ICheckerTest> _checkerTests;
    private readonly CheckerTestResult[] _versionResults;
    private CheckerTestResult[] _checkerTestResults;
    private readonly Dictionary<int, ISimpleChecker> _simpleCheckers;
    private readonly SemaphoreSlim _semaphore = new(1);

    public CheckerService(IRunnerService runner, IEnumerable<ICompilatorConfiguration> compilatorConfigurations,
        IEnumerable<ICheckerTest> checkerTests, IEnumerable<ISimpleChecker> simpleCheckers)
    {
        _runner = runner;
        _checkerTests = [.. checkerTests];
        _simpleCheckers = simpleCheckers.ToDictionary(x => x.Id);
        Configurations = [.. compilatorConfigurations.Where(x => runner.SupportedEnvs.Contains(x.ConfigName))];
        _versionResults = [.. Configurations.Select(configuration => new CheckerTestResult("Version",
            new(configuration, configuration.Name, null), CheckerResultStatus.Accepted, null, string.Empty))];
        _checkerTestResults = CreateTestResults(_versionResults.Select(x => x.Compilator));
    }

    public Guid Id => _runner.Id;
    public string Name => _runner.Name;
    public DateTimeOffset ConnectingTime => _runner.ConnectingTime;
    public IReadOnlyList<ICompilatorConfiguration> Configurations { get; }
    public IReadOnlyList<CompilatorInfo>? Compilators { get; private set; }
    public CheckerSelfTestingStatus SelfTestingStatus { get; private set; } = CheckerSelfTestingStatus.Unknown;
    public IEnumerable<CheckerTestResult> SelfTestingResults => [.. _versionResults, .. _checkerTestResults];

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
            for (int i = 0; i < Configurations.Count; i++)
            {
                var configuration = Configurations[i];
                token.ThrowIfCancellationRequested();
                var command = configuration.GetVersionCommand();
                var output = string.Empty;
                var log = $"Environment: {configuration.ConfigName}\ncommand: {command}";
                CompilatorInfo compilator;
                try
                {
                    var key = (configuration.ConfigName, command);
                    if (!results.TryGetValue(key, out var result))
                    {
                        result = await _runner.Run(new(command, ReadOnlyMemory<byte>.Empty, false, false,
                            [configuration.ConfigName], [], CompilationResources), token);
                        results.Add(key, result);
                    }

                    log = $"""
                        Environment: {configuration.ConfigName}
                        {GetCommandLog(command, result)}
                        stdout:
                        {GetTruncatedString(result.Stdout, 64 * 1024)}
                        stderr:
                        {GetTruncatedString(result.Stderr, 64 * 1024)}
                        """;
                    output = (Encoding.UTF8.GetString(result.Stdout.Span) + "\n"
                        + Encoding.UTF8.GetString(result.Stderr.Span)).Trim();
                    if (result.Status != RunnerStatus.Completed || result.ExitCode != 0)
                        throw new InvalidOperationException($"status: {result.Status}, exit code: {result.ExitCode}");

                    compilator = configuration.ParseInfo(output);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    compilator = new(configuration, configuration.Name, null,
                        $"{command}\n{output}\n{ex.Message}");
                    log += $"\nError:\n{ex.Message}";
                }
                compilators.Add(compilator);
                _versionResults[i] = new("Version", compilator, CheckerResultStatus.Accepted,
                    compilator.Error is null ? CheckerResultStatus.Accepted : CheckerResultStatus.InternalError, log);
            }

            var sorted = compilators.OrderBy(x => x.DisplayName, StringComparer.Ordinal).ToArray();
            _checkerTestResults = CreateTestResults(sorted);
            Compilators = sorted;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<RunnerResult> Compile(string name, ICompilatorConfiguration configuration,
        string source, bool clearWorkdir, CancellationToken token)
    {
        if (!_runner.SupportedEnvs.Contains(configuration.ConfigName))
            throw new NotSupportedException($"Runner {_runner.Name} don't support config {configuration.ConfigName}");

        var filename = configuration.GetSourceFileName(name);
        var command = configuration.GetCompileCommand(name);

        return await _runner.Run(new($"{command} 1>&2", Memory<byte>.Empty, clearWorkdir, false,
            [configuration.ConfigName], [new(filename, Encoding.UTF8.GetBytes(source))], CompilationResources), token);
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
            RunnerStatus.UserTimeLimit => CheckerResultStatus.TimeLimit,
            RunnerStatus.TotalTimeLimit => CheckerResultStatus.IdlenessLimit,
            RunnerStatus.MemoryLimit => CheckerResultStatus.MemoryLimit,
            RunnerStatus.StdoutLimit => CheckerResultStatus.PresentationError,
            RunnerStatus.StderrLimit => CheckerResultStatus.PresentationError,
            RunnerStatus.Error => CheckerResultStatus.RuntimeError,
            RunnerStatus.Completed => checker.Check(encoding.GetString(input.Span),
                encoding.GetString(output.Span), encoding.GetString(result.Stdout.Span)),
            _ => throw new Exception($"Runner unexpected result {result.Status}")
        };
        return (status, result);
    }

    public async Task<CheckerResult> Check(CheckRequest request, CancellationToken token)
    {
        await _semaphore.WaitAsync(token);
        var timer = Stopwatch.StartNew();

        try
        {
            var name = request.Name;
            var compilator = request.Compilator;
            if (compilator.Error is not null)
                throw new NotSupportedException($"Compilator {compilator.Configuration.Name} is unavailable:\n{compilator.Error}");
            var configuration = compilator.Configuration;
            var log = new StringBuilder();
            log.AppendLine($"""
                Runner: {Name} ({Id})
                Compilator: {compilator.DisplayName}
                Configuration: {configuration.Name} ({configuration.ConfigName})
                Versions: {compilator.Description}
                """);

            var compileResult = await Compile(name, configuration, request.Source, true, token);
            log.AppendLine($"""
                Compilation:
                user time: {compileResult.ResourceConsumption.UserTime}
                total time: {compileResult.ResourceConsumption.TotalTime}
                """);
            if (compileResult.Status is not RunnerStatus.Completed)
            {
                return new(CheckerResultStatus.CompilationError.MakeStatusCode(),
                    log.AppendLine(GetTruncatedString(compileResult.Stderr, 1024)).ToString());
            }

            if (!_simpleCheckers.TryGetValue(request.CheckerId, out var checker))
                throw new InvalidOperationException($"Checker with id {request.CheckerId} not found");

            var testResults = new List<CheckerResultStatus>();
            var statusCode = 2;
            var maxUserTime = TimeSpan.Zero;
            var maxTotalTime = TimeSpan.Zero;
            var maxPeakMemory = 0ul;
            foreach (var test in request.Tests)
            {
                int counter = 0;
                log.AppendLine($"""
                    --------------
                    Test {test.Number}
                    """);
            test:
                var (status, result) = await CheckTest(name, configuration, checker,
                    test.Input, test.Output, request.TimeLimit, request.MemoryLimitMb, token);

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

                if (status is CheckerResultStatus.TimeLimit or CheckerResultStatus.MemoryLimit && ++counter < 2)
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

                    if (!request.AllTests)
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

            var needScores = request.Tests.Any(x => x.Score is not null);
            return new(statusCode, log.ToString(),
                !needScores ? null : request.Tests
                    .Zip(testResults, (test, res) => res is CheckerResultStatus.Accepted ? test.Score : 0).Sum(),
                !needScores ? null : string.Join(';', request.Tests
                    .Zip(testResults, (test, res) => $"{(res is CheckerResultStatus.Accepted ? test.Score ?? 0 : res)}")));
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
            var compileResult = await Compile(name, configuration, source, true, token);
            var compilationLog = GetCompilationLog(configuration.GetCompileCommand(name), compileResult);
            if (compileResult.Status is not RunnerStatus.Completed)
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

    private ICheckerTest[] GetTests(ICompilatorConfiguration configuration) =>
        [.. _checkerTests.Where(x => x.Language == configuration.Language
            && (x.CompilatorNames.Count == 0 || x.CompilatorNames.Contains(configuration.Name)))];

    private CheckerTestResult[] CreateTestResults(IEnumerable<CompilatorInfo> compilators)
    {
        var results = new List<CheckerTestResult>();
        foreach (var compilator in compilators)
        {
            var tests = GetTests(compilator.Configuration);
            if (tests.Length == 0)
                results.Add(new("SelfTests", compilator, CheckerResultStatus.Accepted, CheckerResultStatus.InternalError,
                    $"No self-tests for compilator {compilator.Configuration.Name}, language {compilator.Configuration.Language}"));
            else
                results.AddRange(tests.Select(test => new CheckerTestResult(test.Name, compilator,
                    test.ExpectedResult, null, string.Empty)));
        }
        return [.. results];
    }

    public async Task<bool> RunSelfTests(CancellationToken token)
    {
        await LoadCompilators(token);
        var compilators = Compilators!;

        _checkerTestResults = CreateTestResults(compilators);
        SelfTestingStatus = CheckerSelfTestingStatus.InProcess;

        int index = 0;
        foreach (var compilator in compilators)
        {
            var tests = GetTests(compilator.Configuration);
            if (tests.Length == 0)
            {
                index++;
                continue;
            }
            foreach (var test in tests)
            {
                int resultIndex = index++;
                if (compilator.Error is not null)
                    continue;

                token.ThrowIfCancellationRequested();
                await _semaphore.WaitAsync(token);
                var pending = _checkerTestResults[resultIndex];
                try
                {
                    var (status, output) = await RunCheckerTest(test, compilator.Configuration, token);
                    _checkerTestResults[resultIndex] = pending with { Result = status, Output = output };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _checkerTestResults[resultIndex] = pending with { Result = CheckerResultStatus.InternalError, Output = ex.ToString() };
                }
                finally
                {
                    _semaphore.Release();
                }
            }
        }

        bool success = compilators.Count > 0 && SelfTestingResults.All(x => x.Result == x.ExpectedResult);
        SelfTestingStatus = success ? CheckerSelfTestingStatus.Success : CheckerSelfTestingStatus.Failed;
        return success;
    }

    private async Task<(CheckerResultStatus, string)> RunCheckerTest(ICheckerTest test,
        ICompilatorConfiguration configuration, CancellationToken token)
    {
        var encoding = Encoding.UTF8;
        var compileResult = await Compile(test.Name, configuration, test.Source, true, token);
        var compilationLog = GetCompilationLog(configuration.GetCompileCommand(test.Name), compileResult);
        if (compileResult.Status is not RunnerStatus.Completed)
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

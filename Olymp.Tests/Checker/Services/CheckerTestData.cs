using System.Text;

using Olymp.Checker;
using Olymp.Checker.Checkers;
using Olymp.Checker.Services;

namespace Olymp.Tests.Checker.Services;

internal static class CheckerTestData
{
    internal static RunnerResult Completed(string output = "ok", string stderr = "",
        RunnerStatus status = RunnerStatus.Completed, int exitCode = 0, RunnerResources? resources = null) =>
        new(status, exitCode, Encoding.UTF8.GetBytes(output), Encoding.UTF8.GetBytes(stderr),
            resources ?? new(TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0));

    internal sealed class RecordingChecker(Func<string, string, string, CheckerResultStatus> check) : ISimpleChecker
    {
        public int Id => 17;
        public int Calls { get; private set; }

        public CheckerResultStatus Check(string input, string expected, string actual)
        {
            Calls++;
            return check(input, expected, actual);
        }
    }

    internal static CheckerService CreateChecker(TestRunner runner) =>
        new(runner, [new TestConfiguration()], [], [new DefaultStrictChecker()]);

    internal static CheckRequest CreateRequest(bool allTests)
    {
        var configuration = new TestConfiguration();
        int[] scores = [0, 20, 30, 50];
        return new("42", "source", configuration.ParseInfo("1"), 4,
            [.. scores.Select((score, i) => new CheckTest(i + 1,
                Encoding.UTF8.GetBytes((i + 1).ToString()), "ok"u8.ToArray(), score))],
            TimeSpan.FromSeconds(1), 128, allTests);
    }

    internal sealed class TestConfiguration : ICompilatorConfiguration
    {
        public string Name { get; init; } = "test";
        public string? DisplayName { get; init; }
        public bool FailVersionParsing { get; init; }
        public string Language { get; init; } = "C";
        public string ConfigName { get; init; } = "test";
        public string SourceExtension => ".c";
        public string CompileCommand => "compile";
        public bool UseSlowTimeLimit => false;
        public int MemoryOverheadMb { get; init; }
        public string GetSourceFileName(string name) => name + ".c";
        public string GetCompileCommand(string name) => "compile";
        public string RunCommand(string name, int memoryMb) => "run";
        public string VersionCommand { get; init; } = "version";
        public string GetVersionCommand() => VersionCommand;
        public CompilatorInfo ParseInfo(string output) => FailVersionParsing
            ? throw new FormatException("Invalid version") : new(this, DisplayName ?? (Name + " 1"), "full version");
    }

    internal sealed class TestSelfCheck : ICheckerTest
    {
        public string Name { get; init; } = nameof(TestSelfCheck);
        public string Language { get; init; } = "C";
        public IReadOnlyList<string> CompilatorNames { get; init; } = [];
        public string Source { get; init; } = "source";
        public string Input { get; init; } = "";
        public string Output { get; init; } = "ok";
        public ISimpleChecker Checker { get; init; } = new DefaultStrictChecker();
        public CheckerResultStatus ExpectedResult { get; init; } = CheckerResultStatus.Accepted;
    }

    internal sealed class TestRunner : IRunnerService
    {
        public List<RunnerTask> Requests { get; } = [];
        public Func<RunnerTask, CancellationToken, Task<RunnerResult>>? Handler { get; init; }
        public int[] FailureTests { get; init; } = [];
        public RunnerStatus FailureStatus { get; init; } = RunnerStatus.Completed;
        public RunnerStatus VersionStatus { get; init; } = RunnerStatus.Completed;
        public int FailuresBeforeSuccess { get; init; }
        public int PauseBeforeRun { get; init; }
        public TaskCompletionSource? Paused { get; init; }
        public Task? Resume { get; init; }
        public bool CompilationFails { get; init; }
        public bool ThrowOnExecution { get; init; }
        public string CompilerOutput { get; init; } = "compiler output";
        public int TestsRun { get; private set; }
        public int VersionRequests { get; private set; }
        public int Compilations { get; private set; }
        public Guid Id { get; } = Guid.NewGuid();
        public string Name => "Test runner";
        public DateTimeOffset ConnectingTime => DateTimeOffset.Now;
        public IReadOnlySet<string> SupportedEnvs { get; init; } = new HashSet<string> { "test" };

        public async Task<RunnerResult> Run(RunnerTask request, CancellationToken token = default)
        {
            Requests.Add(request);
            if (Handler is not null)
                return await Handler(request, token);
            var compile = request.Command.StartsWith("compile", StringComparison.Ordinal);
            var version = request.Command == "version";
            var run = request.Command == "run";
            if (run && ThrowOnExecution)
                throw new InvalidOperationException("Injected runner failure");
            if (run && TestsRun + 1 == PauseBeforeRun)
            {
                Paused!.TrySetResult();
                await Resume!.WaitAsync(token);
            }
            if (compile)
                Compilations++;
            if (version)
                VersionRequests++;
            var output = run ? (FailureTests.Contains(++TestsRun) ? "wrong" : "ok") : "1";
            var status = compile && CompilationFails ? RunnerStatus.Error
                : version ? VersionStatus
                : run && TestsRun <= FailuresBeforeSuccess ? FailureStatus : RunnerStatus.Completed;
            return new RunnerResult(status, compile && CompilationFails ? 1 : 0,
                Encoding.UTF8.GetBytes(output), compile ? Encoding.UTF8.GetBytes(CompilerOutput) : ReadOnlyMemory<byte>.Empty,
                new(TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0));
        }
    }
}

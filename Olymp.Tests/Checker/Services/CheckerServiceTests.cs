using System.Text;

using Olymp.Checker;
using Olymp.Checker.Services;

using Xunit;

using static Olymp.Tests.Checker.Services.CheckerTestData;

namespace Olymp.Tests.Checker.Services;

public class CheckerServiceTests
{
    [Theory]
    [InlineData(true, 0, 100, 4, "0;20;30;50")]
    [InlineData(true, 1, 100, 4, "WrongAnswer;20;30;50")]
    [InlineData(true, 3, 70, 4, "0;20;WrongAnswer;50")]
    [InlineData(true, 4, 50, 4, "0;20;30;WrongAnswer")]
    [InlineData(false, 3, 20, 3, "0;20;WrongAnswer")]
    public async Task AllTestsControlsStoppingAndScoresAreCalculatedInBothModes(bool allTests, int failureTest,
        int? expectedScore, int expectedRuns, string? expectedResults)
    {
        var runner = new TestRunner { FailureTests = [failureTest] };
        var checker = CreateChecker(runner);

        var result = await checker.Check(CreateRequest(allTests), TestContext.Current.CancellationToken);

        Assert.Equal(expectedRuns, runner.TestsRun);
        Assert.Equal(expectedScore, result.Score);
        Assert.Equal(expectedResults, result.TestResults);
        Assert.Equal(failureTest == 0 ? 2 : -1000 - failureTest, result.StatusCode);
        if (expectedRuns < 4)
            Assert.DoesNotContain($"Test {expectedRuns + 1}", result.Log);
    }

    [Fact]
    public async Task AllTestsKeepsFirstFailureAndScoresSuccessesAfterFailures()
    {
        var runner = new TestRunner { FailureTests = [1, 3] };
        var checker = CreateChecker(runner);

        var result = await checker.Check(CreateRequest(true), TestContext.Current.CancellationToken);

        Assert.Equal(4, runner.TestsRun);
        Assert.Equal(-1001, result.StatusCode);
        Assert.Equal(70, result.Score);
        Assert.Equal("WrongAnswer;20;WrongAnswer;50", result.TestResults);
    }

    [Theory]
    [InlineData(RunnerStatus.UserTimeLimit, 2, 2, -2001)]
    [InlineData(RunnerStatus.MemoryLimit, 2, 2, -6001)]
    [InlineData(RunnerStatus.UserTimeLimit, 1, 5, 2)]
    [InlineData(RunnerStatus.MemoryLimit, 1, 5, 2)]
    public async Task ResourceFailuresGetAtMostTwoAttempts(RunnerStatus failureStatus, int failuresBeforeSuccess,
        int expectedRuns, int expectedStatus)
    {
        var runner = new TestRunner { FailureStatus = failureStatus, FailuresBeforeSuccess = failuresBeforeSuccess };
        var checker = CreateChecker(runner);

        var result = await checker.Check(CreateRequest(false), TestContext.Current.CancellationToken);

        Assert.Equal(expectedRuns, runner.TestsRun);
        Assert.Equal(expectedStatus, result.StatusCode);
    }

    [Fact]
    public async Task CompilationFailureKeepsBoundedDiagnosticsAndSkipsExecution()
    {
        string diagnostic = new('E', 1024);
        var runner = new TestRunner { CompilationFails = true, CompilerOutput = diagnostic + "Discarded output" };
        var checker = CreateChecker(runner);

        var result = await checker.Check(CreateRequest(true), TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.StatusCode);
        Assert.Equal(0, runner.TestsRun);
        Assert.Null(result.Score);
        Assert.Null(result.TestResults);
        Assert.Contains("Compilation:", result.Log);
        Assert.Contains("user time:", result.Log);
        Assert.Contains(diagnostic, result.Log);
        Assert.DoesNotContain("Discarded output", result.Log);
    }

    [Theory]
    [InlineData(RunnerStatus.Completed, 2, 1)]
    [InlineData(RunnerStatus.Error, -3007, 1)]
    [InlineData(RunnerStatus.UserTimeLimit, -2007, 2)]
    [InlineData(RunnerStatus.TotalTimeLimit, -5007, 1)]
    [InlineData(RunnerStatus.MemoryLimit, -6007, 2)]
    [InlineData(RunnerStatus.StdoutLimit, -4007, 1)]
    [InlineData(RunnerStatus.StderrLimit, -4007, 1)]
    public async Task RunnerFailuresTakePrecedenceOverMatchingOutput(RunnerStatus status,
        int expectedStatus, int attempts)
    {
        var comparison = new RecordingChecker((_, _, _) => CheckerResultStatus.Accepted);
        var runner = new TestRunner
        {
            Handler = (request, _) => Task.FromResult(Completed(status:
                request.Command == "run" ? status : RunnerStatus.Completed))
        };
        var checker = new CheckerService(runner, [new TestConfiguration()], [], [comparison]);
        var request = CreateRequest(false) with
        {
            CheckerId = comparison.Id,
            Tests = [new(7, "input"u8.ToArray(), "ok"u8.ToArray(), null)]
        };

        var result = await checker.Check(request, TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, result.StatusCode);
        Assert.Equal(attempts, runner.Requests.Count(x => x.Command == "run"));
        Assert.Equal(status == RunnerStatus.Completed ? 1 : 0, comparison.Calls);
    }

    [Theory]
    [InlineData(RunnerStatus.Error)]
    [InlineData(RunnerStatus.UserTimeLimit)]
    [InlineData(RunnerStatus.TotalTimeLimit)]
    [InlineData(RunnerStatus.MemoryLimit)]
    [InlineData(RunnerStatus.StdoutLimit)]
    [InlineData(RunnerStatus.StderrLimit)]
    public async Task UnsuccessfulCompilationNeverExecutesTests(RunnerStatus status)
    {
        var runner = new TestRunner { Handler = (_, _) => Task.FromResult(Completed(status: status)) };

        var result = await CreateChecker(runner).Check(CreateRequest(true), TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.StatusCode);
        Assert.Single(runner.Requests);
        Assert.Null(result.Score);
        Assert.Null(result.TestResults);
    }

    [Fact]
    public async Task CheckPreservesUtf8SourceRawInputAndWhitespaceAndUsesSelectedChecker()
    {
        const string source = "// Кириллица\r\nsource";
        const string input = "\0  Привет\r\n\nlast  ";
        const string output = "  Мир\r\n\tlast  ";
        var comparison = new RecordingChecker((actualInput, expected, actual) =>
        {
            Assert.Equal(input, actualInput);
            Assert.Equal(output, expected);
            Assert.Equal(output, actual);
            return CheckerResultStatus.Accepted;
        });
        var runner = new TestRunner { Handler = (_, _) => Task.FromResult(Completed(output)) };
        var configuration = new TestConfiguration { MemoryOverheadMb = 64 };
        var checker = new CheckerService(runner, [configuration], [], [comparison]);
        var request = new CheckRequest("submission", source, configuration.ParseInfo("1"), comparison.Id,
            [new(1, Encoding.UTF8.GetBytes(input), Encoding.UTF8.GetBytes(output), null)],
            TimeSpan.FromSeconds(2), 128, false);

        var result = await checker.Check(request, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.StatusCode);
        Assert.Equal(1, comparison.Calls);
        Assert.Equal(2, runner.Requests.Count);
        var compile = runner.Requests[0];
        Assert.True(compile.ClearWorkdir);
        Assert.False(compile.ReadOnly);
        Assert.True(compile.Stdin.IsEmpty);
        Assert.Equal([configuration.ConfigName], compile.EnvFiles);
        var file = Assert.Single(compile.Files);
        Assert.Equal("submission.c", file.Filename);
        Assert.Equal(Encoding.UTF8.GetBytes(source), file.Content.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(30), compile.ResourceLimits.UserTime);
        Assert.Equal(TimeSpan.FromSeconds(30), compile.ResourceLimits.TotalTime);
        Assert.Equal(768ul * 1024 * 1024, compile.ResourceLimits.Memory);
        Assert.Equal(64ul * 1024, compile.ResourceLimits.Stdout);
        Assert.Equal(64ul * 1024, compile.ResourceLimits.Stderr);
        var run = runner.Requests[1];
        Assert.False(run.ClearWorkdir);
        Assert.True(run.ReadOnly);
        Assert.Empty(run.Files);
        Assert.Equal([configuration.ConfigName], run.EnvFiles);
        Assert.Equal(Encoding.UTF8.GetBytes(input), run.Stdin.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(2), run.ResourceLimits.UserTime);
        Assert.Equal(TimeSpan.FromSeconds(7), run.ResourceLimits.TotalTime);
        Assert.Equal(192ul * 1024 * 1024, run.ResourceLimits.Memory);
        Assert.Equal(1024ul * 1024, run.ResourceLimits.Stdout);
        Assert.Equal(4096ul, run.ResourceLimits.Stderr);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnscoredProblemsDoNotProduceScores(bool allTests)
    {
        var request = CreateRequest(allTests);
        request = request with { Tests = [.. request.Tests.Select(test => test with { Score = null })] };

        var result = await CreateChecker(new TestRunner()).Check(request, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.StatusCode);
        Assert.Null(result.Score);
        Assert.Null(result.TestResults);
    }

    [Fact]
    public async Task NullAndZeroScoresDoNotHideSuccessfulOrFailedTests()
    {
        var request = CreateRequest(true);
        int?[] scores = [null, 0, 20, null];
        request = request with { Tests = [.. request.Tests.Select((test, i) => test with { Score = scores[i] })] };

        var result = await CreateChecker(new TestRunner { FailureTests = [2] })
            .Check(request, TestContext.Current.CancellationToken);

        Assert.Equal(-1002, result.StatusCode);
        Assert.Equal(20, result.Score);
        Assert.Equal("0;WrongAnswer;20;0", result.TestResults);
    }

    [Fact]
    public async Task FailureUsesTestNumberAndDoesNotScoreTestsThatWereNotRun()
    {
        var request = CreateRequest(false);
        request = request with
        {
            Tests = [request.Tests[0] with { Number = 19 }, request.Tests[1] with { Number = 42 }]
        };

        var result = await CreateChecker(new TestRunner { FailureTests = [1] })
            .Check(request, TestContext.Current.CancellationToken);

        Assert.Equal(-1019, result.StatusCode);
        Assert.Equal(0, result.Score);
        Assert.Equal("WrongAnswer", result.TestResults);
        Assert.DoesNotContain("Test 42", result.Log);
    }

    [Fact]
    public async Task SuccessfulChecksLogCompilationTimeWithoutCompilerOutput()
    {
        var runner = new TestRunner { CompilerOutput = "Successful compilation diagnostics" };

        var result = await CreateChecker(runner).Check(CreateRequest(false), TestContext.Current.CancellationToken);

        Assert.Contains("Compilation:", result.Log);
        Assert.Contains("user time:", result.Log);
        Assert.DoesNotContain(runner.CompilerOutput, result.Log);
    }

    [Fact]
    public async Task FailureDiagnosticsAreBoundedForBothStreams()
    {
        var runner = new TestRunner
        {
            Handler = (request, _) => Task.FromResult(request.Command == "run"
                ? Completed(new string('O', 1024) + "stdout tail", new string('E', 1024) + "stderr tail",
                    RunnerStatus.Error, 1) : Completed())
        };

        var result = await CreateChecker(runner).Check(CreateRequest(false), TestContext.Current.CancellationToken);

        Assert.Equal(-3001, result.StatusCode);
        Assert.Contains(new string('O', 1024), result.Log);
        Assert.Contains(new string('E', 1024), result.Log);
        Assert.DoesNotContain("stdout tail", result.Log);
        Assert.DoesNotContain("stderr tail", result.Log);
    }

    [Fact]
    public async Task InvalidCheckerDoesNotPreventLaterChecks()
    {
        var runner = new TestRunner();
        var checker = CreateChecker(runner);
        var token = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidOperationException>(() => checker.Check(
            CreateRequest(false) with { CheckerId = 999 }, token));
        Assert.Equal(0, runner.TestsRun);
        var result = await checker.Check(CreateRequest(false), token);

        Assert.Equal(2, result.StatusCode);
    }

    [Fact]
    public async Task ResourceSummaryIncludesRetriedAttemptsButScoreCountsTestOnce()
    {
        int attempts = 0;
        var runner = new TestRunner
        {
            Handler = (request, _) => Task.FromResult(request.Command != "run" ? Completed()
                : ++attempts == 1
                    ? Completed(status: RunnerStatus.UserTimeLimit, resources:
                        new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), 16 * 1024 * 1024, 0, 0))
                    : Completed(resources:
                        new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 32 * 1024 * 1024, 0, 0)))
        };
        var request = CreateRequest(false);
        request = request with { Tests = [request.Tests[1]] };

        var result = await CreateChecker(runner).Check(request, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.StatusCode);
        Assert.Equal(20, result.Score);
        Assert.Equal("20", result.TestResults);
        Assert.Contains("Max user time: 00:00:03", result.Log);
        Assert.Contains("Max total time: 00:00:04", result.Log);
        Assert.Contains("Max peak memory: 32 MB", result.Log);
    }

    [Fact]
    public async Task CheckRejectsUnavailableCompilatorBeforeCompilation()
    {
        var runner = new TestRunner();
        var checker = CreateChecker(runner);
        var request = CreateRequest(false);
        request = request with { Compilator = request.Compilator with { Error = "Version request failed" } };

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => checker.Check(request,
            TestContext.Current.CancellationToken));

        Assert.Contains("Version request failed", error.Message);
        Assert.Equal(0, runner.Compilations);
    }
}

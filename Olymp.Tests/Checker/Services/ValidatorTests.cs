using Olymp.Checker;

using Xunit;

using static Olymp.Tests.Checker.Services.CheckerTestData;

namespace Olymp.Tests.Checker.Services;

public class ValidatorTests
{
    [Fact]
    public async Task LogKeepsTestsCompactAndIncludesOnlyFailureDiagnostics()
    {
        var runner = new TestRunner
        {
            Handler = (task, _) => Task.FromResult(task.Command == "run"
                ? task.Stdin.Span[0] == 2
                    ? Completed(status: RunnerStatus.Error, exitCode: 3, stderr: "FAIL bad input\n\n")
                    : Completed(stderr: "successful diagnostic")
                : Completed())
        };
        var result = await CreateChecker(runner).Validate("source", new TestConfiguration { Language = "C++" }.ParseInfo("1"),
            [new(1, new byte[] { 1 }), new(2, new byte[] { 2 }), new(3, new byte[] { 3 })], TestContext.Current.CancellationToken);

        var testsLog = result.Log[result.Log.IndexOf("--------------" + Environment.NewLine + "Test 1:", StringComparison.Ordinal)..];
        Assert.Equal(string.Join(Environment.NewLine,
            "--------------",
            "Test 1: Completed, exit code 0",
            "Test 2: Error, exit code 3",
            "FAIL bad input",
            "Test 3: Completed, exit code 0", ""), testsLog);
        Assert.DoesNotContain("compilator:", result.Log);
    }

    [Fact]
    public async Task CompilesWithEmbeddedHeaderPreservesInputBytesAndReportsExitCodes()
    {
        var runner = new TestRunner
        {
            Handler = (task, _) => Task.FromResult(task.Command == "run" ? Completed(status: RunnerStatus.Error, exitCode: 3, stderr: "bad input") : Completed())
        };
        var compiler = new TestConfiguration { Language = "C++" }.ParseInfo("1");
        byte[] input = [0, 255, 13, 10];
        var result = await CreateChecker(runner).Validate("source", compiler,
            [new(7, input)], TestContext.Current.CancellationToken);
        Assert.True(result.Compiled);
        var test = Assert.Single(result.Tests);
        Assert.Equal(7, test.Number);
        Assert.False(test.IsValid);
        Assert.Contains("exit code 3", result.Log);
        Assert.Equal(runner.Requests[0].ResourceLimits, runner.Requests[1].ResourceLimits);
        Assert.Equal(input, runner.Requests[1].Stdin.ToArray());
        var header = Assert.Single(runner.Requests[0].Files, f => f.Filename == "testlib.h");
        Assert.True(header.Content.Length > 100000);
        Assert.Contains("bad input", result.Log);
    }

    [Fact]
    public async Task FailedCompilationDoesNotRunValidator()
    {
        var diagnostic = "compile error" + new string('E', 1024 - "compile error".Length);
        var runner = new TestRunner { Handler = (_, _) => Task.FromResult(Completed(status: RunnerStatus.Error, exitCode: 1, stderr: diagnostic + "discarded diagnostic")) };
        var result = await CreateChecker(runner).Validate("source", new TestConfiguration { Language = "C++" }.ParseInfo("1"),
            [new(1, new byte[] { 1 })], TestContext.Current.CancellationToken);
        Assert.False(result.Compiled);
        Assert.EndsWith("compilator:" + Environment.NewLine + diagnostic + Environment.NewLine, result.Log);
        Assert.DoesNotContain("discarded diagnostic", result.Log);
        Assert.Empty(result.Tests);
        Assert.Single(runner.Requests);
    }

    [Theory]
    [InlineData(RunnerStatus.UserTimeLimit)]
    [InlineData(RunnerStatus.MemoryLimit)]
    [InlineData(RunnerStatus.StderrLimit)]
    public async Task ReportsFailedTestAndContinuesWithRemainingInputs(RunnerStatus status)
    {
        var runner = new TestRunner
        {
            Handler = (task, _) => Task.FromResult(task.Command == "run" && task.Stdin.Span[0] == 1
                ? Completed(status: status) : Completed())
        };
        var result = await CreateChecker(runner).Validate("source", new TestConfiguration { Language = "C++" }.ParseInfo("1"),
            [new(1, new byte[] { 1 }), new(2, new byte[] { 2 })], TestContext.Current.CancellationToken);

        Assert.True(result.Compiled);
        Assert.Collection(result.Tests,
            test => Assert.False(test.IsValid),
            test => Assert.True(test.IsValid));
        Assert.Contains(status.ToString(), result.Log);
    }

    [Fact]
    public async Task CancelledValidationReleasesRunnerForNextOperation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bool cancel = true;
        var runner = new TestRunner
        {
            Handler = (task, token) =>
            {
                if (task.Command == "run" && cancel)
                {
                    cancel = false;
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return Task.FromResult(Completed());
            }
        };
        var checker = CreateChecker(runner);
        var compiler = new TestConfiguration { Language = "C++" }.ParseInfo("1");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.Validate("source", compiler,
            [new(1, "input"u8.ToArray())], cancellation.Token));
        var result = await checker.Run("source", new TestConfiguration(), "input", TimeSpan.FromSeconds(1), 128,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, result.StatusCode);
    }

    [Fact]
    public async Task SolutionMetricsIdentifySlowestTestAndPeakMemory()
    {
        var runner = new TestRunner
        {
            Handler = (task, _) => Task.FromResult(task.Command == "run" ? Completed(resources:
                new(TimeSpan.FromMilliseconds(task.Stdin.Span[0]), TimeSpan.FromMilliseconds(100 - task.Stdin.Span[0]),
                    (ulong)task.Stdin.Span[0] * 1024, 0, 0)) : Completed())
        };
        var result = await CreateChecker(runner).Check(CreateRequest(true), TestContext.Current.CancellationToken);
        Assert.Equal(4, result.SlowestTest);
        Assert.Equal(TimeSpan.FromMilliseconds((byte)'4'), result.MaxUserTime);
        Assert.Equal(TimeSpan.FromMilliseconds(100 - (byte)'1'), result.MaxTotalTime);
        Assert.Equal((ulong)'4' * 1024, result.MaxMemory);
    }
}

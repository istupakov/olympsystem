using System.Text;

using Olymp.Checker;

using Xunit;

using static Olymp.Tests.Checker.Services.CheckerTestData;

namespace Olymp.Tests.Checker.Services;

public class CheckerRunTests
{
    [Fact]
    public async Task FailedCompilationShowsDiagnosticsAndSkipsExecution()
    {
        var diagnostic = new string('E', 100 * 1024);
        var runner = new TestRunner { CompilationFails = true, CompilerOutput = diagnostic + "discarded diagnostic" };

        var result = await CreateChecker(runner).Run("source", new TestConfiguration(), "input",
            TimeSpan.FromSeconds(1), 128, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.StatusCode);
        Assert.Null(result.MaxUserTime);
        Assert.Null(result.MaxTotalTime);
        Assert.Null(result.MaxMemory);
        Assert.Contains("--------------" + Environment.NewLine + "Compilation" + Environment.NewLine, result.Log);
        Assert.Contains(diagnostic, result.Log);
        Assert.DoesNotContain("discarded diagnostic", result.Log);
        Assert.Contains("exit code: 1", result.Log);
        Assert.DoesNotContain("Execution:", result.Log);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task RunPreservesSourceAndInputAndShowsBoundedOutputWithResourceUsage()
    {
        const string source = "// Кириллица\nsource";
        const string input = "  мир  \r\nlast";
        var resources = new RunnerResources(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), 32 * 1024 * 1024, 0, 0);
        var runner = new TestRunner
        {
            Handler = (request, _) => Task.FromResult(request.Command == "run"
                ? Completed(new string('O', 4096) + "stdout tail", new string('E', 4096) + "stderr tail",
                    RunnerStatus.Error, 42, resources)
                : Completed(stderr: "compiler warnings"))
        };

        var result = await CreateChecker(runner).Run(source, new TestConfiguration(), input,
            TimeSpan.FromSeconds(5), 128, TestContext.Current.CancellationToken);

        Assert.Equal(-3001, result.StatusCode);
        Assert.Equal(resources.UserTime, result.MaxUserTime);
        Assert.Equal(resources.TotalTime, result.MaxTotalTime);
        Assert.Equal(resources.Memory, result.MaxMemory);
        Assert.Null(result.SlowestTest);
        Assert.Null(result.Score);
        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal(Encoding.UTF8.GetBytes(source), Assert.Single(runner.Requests[0].Files).Content.ToArray());
        Assert.Equal(Encoding.UTF8.GetBytes(input), runner.Requests[1].Stdin.ToArray());
        Assert.Contains("compiler warnings", result.Log);
        Assert.Contains("status: Error", result.Log);
        Assert.Contains("user time: 00:00:02", result.Log);
        Assert.Contains("total time: 00:00:03", result.Log);
        Assert.Contains("peak memory: 32 MiB", result.Log);
        Assert.Contains("exit code: 42", result.Log);
        Assert.Contains(new string('O', 4096), result.Log);
        Assert.Contains(new string('E', 4096), result.Log);
        Assert.DoesNotContain("stdout tail", result.Log);
        Assert.DoesNotContain("stderr tail", result.Log);
    }

    [Theory]
    [InlineData(RunnerStatus.Completed, 2)]
    [InlineData(RunnerStatus.UserTimeLimit, -2001)]
    [InlineData(RunnerStatus.TotalTimeLimit, -5001)]
    [InlineData(RunnerStatus.MemoryLimit, -6001)]
    [InlineData(RunnerStatus.StdoutLimit, -4001)]
    [InlineData(RunnerStatus.StderrLimit, -4001)]
    [InlineData(RunnerStatus.Unspecified, -10)]
    public async Task RunReturnsExecutionStatus(RunnerStatus status, int expected)
    {
        var runner = new TestRunner
        {
            Handler = (task, _) => Task.FromResult(Completed(status: task.Command == "run" ? status : RunnerStatus.Completed))
        };
        var result = await CreateChecker(runner).Run("source", new TestConfiguration(), "input",
            TimeSpan.FromSeconds(1), 128, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringExecutionPropagatesAndDoesNotBlockNextOperation(bool manualRun)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bool cancel = true;
        var runner = new TestRunner
        {
            Handler = (request, token) =>
            {
                if (request.Command == "run" && cancel)
                {
                    cancel = false;
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return Task.FromResult(Completed());
            }
        };
        var checker = CreateChecker(runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (manualRun)
                await checker.Run("source", new TestConfiguration(), "input", TimeSpan.FromSeconds(1), 128, cancellation.Token);
            else
                await checker.Check(CreateRequest(false), cancellation.Token);
        });
        var result = await checker.Check(CreateRequest(false), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.StatusCode);
    }

    [Fact]
    public async Task ConcurrentOperationsDoNotInterleaveCompilationAndExecution()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var runner = new TestRunner
        {
            Handler = async (_, token) =>
            {
                if (++calls == 1)
                {
                    entered.SetResult();
                    await resume.Task.WaitAsync(token);
                }
                return Completed();
            }
        };
        var checker = CreateChecker(runner);
        var token = TestContext.Current.CancellationToken;
        var request = CreateRequest(false);
        var first = checker.Check(request with { Tests = [request.Tests[0]] }, token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var second = checker.Run("source", new TestConfiguration(), "input", TimeSpan.FromSeconds(1), 128, token);
        try
        {
            Assert.False(second.IsCompleted);
            Assert.Single(runner.Requests);
        }
        finally
        {
            resume.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        Assert.Equal(["compile 1>&2", "run", "compile 1>&2", "run"], runner.Requests.Select(request => request.Command));
    }

    [Fact]
    public async Task CancelledWaitingOperationDoesNotUseRunnerOrReleaseAnotherOperationsLock()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var runner = new TestRunner
        {
            Handler = async (_, token) =>
            {
                if (++calls == 1)
                {
                    entered.SetResult();
                    await resume.Task.WaitAsync(token);
                }
                return Completed();
            }
        };
        var checker = CreateChecker(runner);
        var token = TestContext.Current.CancellationToken;
        var first = checker.Run("source", new TestConfiguration(), "input", TimeSpan.FromSeconds(1), 128, token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<CheckerResult>? next = null;
        try
        {
            var waiting = checker.Check(CreateRequest(false), cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Single(runner.Requests);
            next = checker.Check(CreateRequest(false), token);
            Assert.False(next.IsCompleted);
            Assert.Single(runner.Requests);
        }
        finally
        {
            resume.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            if (next is not null)
                await next.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        Assert.NotNull(next);
        var result = await next;
        Assert.Equal(2, result.StatusCode);
    }

    [Fact]
    public async Task UnsupportedEnvironmentIsRejectedBeforeRunnerIsCalled()
    {
        var runner = new TestRunner { SupportedEnvs = new HashSet<string>() };
        var checker = CreateChecker(runner);

        await Assert.ThrowsAsync<NotSupportedException>(() => checker.Run("source", new TestConfiguration(), "input",
            TimeSpan.FromSeconds(1), 128, TestContext.Current.CancellationToken));

        Assert.Empty(runner.Requests);
    }
}

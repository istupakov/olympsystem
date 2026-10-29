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
        var runner = new TestRunner { CompilationFails = true };

        var log = await CreateChecker(runner).Run("source", new TestConfiguration(), "input",
            TimeSpan.FromSeconds(1), 128, TestContext.Current.CancellationToken);

        Assert.Contains("Compilation:", log);
        Assert.Contains(runner.CompilerOutput, log);
        Assert.Contains("exit code: 1", log);
        Assert.DoesNotContain("Execution:", log);
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

        var log = await CreateChecker(runner).Run(source, new TestConfiguration(), input,
            TimeSpan.FromSeconds(5), 128, TestContext.Current.CancellationToken);

        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal(Encoding.UTF8.GetBytes(source), Assert.Single(runner.Requests[0].Files).Content.ToArray());
        Assert.Equal(Encoding.UTF8.GetBytes(input), runner.Requests[1].Stdin.ToArray());
        Assert.Contains("compiler warnings", log);
        Assert.Contains("status: Error", log);
        Assert.Contains("user time: 00:00:02", log);
        Assert.Contains("total time: 00:00:03", log);
        Assert.Contains("peak memory: 32 MB", log);
        Assert.Contains("exit code: 42", log);
        Assert.Contains(new string('O', 4096), log);
        Assert.Contains(new string('E', 4096), log);
        Assert.DoesNotContain("stdout tail", log);
        Assert.DoesNotContain("stderr tail", log);
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

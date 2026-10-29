using Olymp.Checker;
using Olymp.Checker.Compilators;
using Olymp.Checker.Services;

using Xunit;

using static Olymp.Tests.Checker.Services.CheckerTestData;

namespace Olymp.Tests.Checker.Services;

public class CheckerSelfTestingTests
{
    [Fact]
    public async Task VersionRequestsAreSharedAndCachedAndCompilatorsAreSorted()
    {
        var runner = new TestRunner();
        var checker = new CheckerService(runner,
            [new TestConfiguration { Name = "Z" }, new TestConfiguration { Name = "A" },
                new TestConfiguration { Name = "Unavailable", ConfigName = "missing" }], [new TestSelfCheck()], []);

        Assert.Equal(["Z", "A"], checker.Configurations.Select(x => x.Name));
        Assert.Null(checker.Compilators);
        Assert.Equal(0, runner.VersionRequests);
        Assert.Equal(4, checker.SelfTestingResults.Count());
        Assert.All(checker.SelfTestingResults, result => Assert.Null(result.Result));
        await checker.LoadCompilators(TestContext.Current.CancellationToken);
        await checker.LoadCompilators(TestContext.Current.CancellationToken);

        Assert.Equal(1, runner.VersionRequests);
        Assert.Equal(["A 1", "Z 1"], checker.Compilators!.Select(x => x.DisplayName));
        Assert.Equal(4, checker.SelfTestingResults.Count());
        Assert.All(checker.SelfTestingResults.Where(x => x.Name == nameof(TestSelfCheck)), result => Assert.Null(result.Result));
        Assert.All(checker.SelfTestingResults.Where(x => x.Name == "Version"), result =>
        {
            Assert.Equal("Version", result.Name);
            Assert.Equal(CheckerResultStatus.Accepted, result.ExpectedResult);
            Assert.Equal(CheckerResultStatus.Accepted, result.Result);
            Assert.Contains("command: version", result.Output);
            Assert.Contains("peak memory:", result.Output);
            Assert.Contains("stdout:\n1", result.Output.Replace("\r\n", "\n"));
        });
    }

    [Fact]
    public async Task FailedVersionRequestRemainsVisibleAndRunDoesNotDependOnIt()
    {
        var runner = new TestRunner { VersionStatus = RunnerStatus.UserTimeLimit };
        var configuration = new TestConfiguration();
        var checker = new CheckerService(runner, [configuration], [], []);
        await checker.LoadCompilators(TestContext.Current.CancellationToken);

        Assert.Contains("UserTimeLimit", Assert.Single(checker.Compilators!).Error);
        Assert.Same(configuration, Assert.Single(checker.Configurations));
        var version = Assert.Single(checker.SelfTestingResults, x => x.Name == "Version");
        Assert.Equal("Version", version.Name);
        Assert.Equal(CheckerResultStatus.InternalError, version.Result);
        Assert.Contains("UserTimeLimit", version.Output);
        var output = await checker.Run("source", configuration, "input", TimeSpan.FromSeconds(1), 128,
            TestContext.Current.CancellationToken);

        Assert.Contains("Compilation:", output);
        Assert.Contains("Execution:", output);
        Assert.Contains("compiler output", output);
        Assert.Equal(1, runner.VersionRequests);
        Assert.Equal(1, runner.TestsRun);
    }

    [Theory]
    [InlineData(false, CheckerSelfTestingStatus.Success, CheckerResultStatus.Accepted)]
    [InlineData(true, CheckerSelfTestingStatus.Failed, CheckerResultStatus.InternalError)]
    public async Task SelfTestsRecordVerdictsAndExecutionDiagnostics(bool fail,
        CheckerSelfTestingStatus expectedStatus, CheckerResultStatus expectedResult)
    {
        var runner = new TestRunner { ThrowOnExecution = fail };
        var checker = new CheckerService(runner, [new TestConfiguration()], [new TestSelfCheck()], []);

        Assert.Equal(!fail, await checker.RunSelfTests(TestContext.Current.CancellationToken));

        Assert.Equal(expectedStatus, checker.SelfTestingStatus);
        var result = Assert.Single(checker.SelfTestingResults, x => x.Name == nameof(TestSelfCheck));
        Assert.Equal(expectedResult, result.Result);
        Assert.Contains(fail ? "Injected runner failure" : "compiler output", result.Output);
    }

    [Fact]
    public async Task MissingSelfTestsFailSelfTesting()
    {
        var checker = CreateChecker(new TestRunner());

        Assert.False(await checker.RunSelfTests(TestContext.Current.CancellationToken));

        Assert.Equal(CheckerSelfTestingStatus.Failed, checker.SelfTestingStatus);
        Assert.Equal(2, checker.SelfTestingResults.Count());
        var error = Assert.Single(checker.SelfTestingResults, x => x.Name == "SelfTests");
        Assert.Equal(CheckerResultStatus.InternalError, error.Result);
        Assert.Contains("No self-tests", error.Output);
    }

    [Fact]
    public async Task RepeatedSelfTestingKeepsVersionResultWithoutRepeatingProbe()
    {
        var runner = new TestRunner();
        var checker = new CheckerService(runner, [new TestConfiguration()], [new TestSelfCheck()], []);
        await checker.LoadCompilators(TestContext.Current.CancellationToken);
        var version = Assert.Single(checker.SelfTestingResults, x => x.Name == "Version");

        Assert.True(await checker.RunSelfTests(TestContext.Current.CancellationToken));
        Assert.True(await checker.RunSelfTests(TestContext.Current.CancellationToken));

        Assert.Equal(1, runner.VersionRequests);
        Assert.Equal(2, runner.TestsRun);
        Assert.Equal(2, checker.SelfTestingResults.Count());
        Assert.Same(version, checker.SelfTestingResults.Single(x => x.Name == "Version"));
    }

    [Fact]
    public async Task VersionParsingFailureFailsItsResultAndSkipsProgramTestsForThatCompilator()
    {
        var runner = new TestRunner();
        var checker = new CheckerService(runner,
            [new TestConfiguration { Name = "Good" }, new TestConfiguration { Name = "Bad", FailVersionParsing = true }],
            [new TestSelfCheck()], []);

        Assert.False(await checker.RunSelfTests(TestContext.Current.CancellationToken));

        Assert.Equal(1, runner.VersionRequests);
        Assert.Equal(1, runner.TestsRun);
        Assert.Equal(CheckerSelfTestingStatus.Failed, checker.SelfTestingStatus);
        var result = Assert.Single(checker.SelfTestingResults, x => x.Compilator.Configuration.Name == "Bad" && x.Name == "Version");
        Assert.Null(checker.SelfTestingResults.Single(x => x.Compilator.Configuration.Name == "Bad" && x.Name == nameof(TestSelfCheck)).Result);
        Assert.Equal("Version", result.Name);
        Assert.Equal(CheckerResultStatus.InternalError, result.Result);
        Assert.Contains("status: Completed", result.Output);
        Assert.Contains("Invalid version", result.Output);
        Assert.Contains("Invalid version", result.Compilator.Error);
    }

    [Fact]
    public async Task ResultsStayVisibleAndSnapshotsStayUnchangedWhileTestsComplete()
    {
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new TestRunner { PauseBeforeRun = 2, Paused = paused, Resume = resume.Task };
        var checker = new CheckerService(runner, [new TestConfiguration()],
            [new TestSelfCheck { Name = "First" }, new TestSelfCheck { Name = "Second" }], []);
        var token = TestContext.Current.CancellationToken;
        var checking = checker.RunSelfTests(token);
        CheckerTestResult[] snapshot = [];
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            snapshot = [.. checker.SelfTestingResults];
            Assert.Equal(3, snapshot.Length);
            Assert.Equal(CheckerSelfTestingStatus.InProcess, checker.SelfTestingStatus);
            Assert.Equal(CheckerResultStatus.Accepted, snapshot.Single(x => x.Name == "First").Result);
            var pending = snapshot.Single(x => x.Name == "Second");
            Assert.Null(pending.Result);
            Assert.Empty(pending.Output);
        }
        finally
        {
            resume.TrySetResult();
            await checking.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        Assert.True(await checking);
        Assert.Null(snapshot.Single(x => x.Name == "Second").Result);
        Assert.Equal(3, checker.SelfTestingResults.Count());
        Assert.All(checker.SelfTestingResults, result => Assert.Equal(result.ExpectedResult, result.Result));
    }

    [Fact]
    public async Task VersionCacheSeparatesEnvironmentsAndCommands()
    {
        var runner = new TestRunner
        {
            SupportedEnvs = new HashSet<string> { "test", "other" },
            Handler = (_, _) => Task.FromResult(Completed("1"))
        };
        var checker = new CheckerService(runner,
            [new TestConfiguration { Name = "A" }, new TestConfiguration { Name = "B" },
                new TestConfiguration { Name = "C", ConfigName = "other" },
                new TestConfiguration { Name = "D", VersionCommand = "another version" }], [], []);

        await checker.LoadCompilators(TestContext.Current.CancellationToken);

        Assert.Equal(3, runner.Requests.Count);
        Assert.Equal(3, runner.Requests.Select(request => (Assert.Single(request.EnvFiles), request.Command)).Distinct().Count());
        Assert.Equal(4, checker.Compilators!.Count);
        Assert.All(checker.Compilators, compilator => Assert.Null(compilator.Error));
    }

    [Fact]
    public async Task CompilersAreSortedByDisplayNameWithOrdinalPunctuationOrder()
    {
        var checker = new CheckerService(new TestRunner(),
            [new TestConfiguration { Name = "A", DisplayName = "C++ (GCC)" },
                new TestConfiguration { Name = "B", DisplayName = "C# (.NET)" },
                new TestConfiguration { Name = "Z", DisplayName = "C (GCC)" }], [], []);

        await checker.LoadCompilators(TestContext.Current.CancellationToken);

        Assert.Equal(["C (GCC)", "C# (.NET)", "C++ (GCC)"], checker.Compilators!.Select(compilator => compilator.DisplayName));
    }

    [Fact]
    public async Task VersionInformationCanComeFromBothStandardStreams()
    {
        var runner = new TestRunner
        {
            SupportedEnvs = new HashSet<string> { "kotlin" },
            Handler = (_, _) => Task.FromResult(Completed("openjdk version \"27.0.1\"\n",
                "info: kotlinc-jvm 2.4.21 (JRE 27.0.1)\n"))
        };
        var checker = new CheckerService(runner, [new KotlinConfiguration()], [], []);

        await checker.LoadCompilators(TestContext.Current.CancellationToken);

        var compilator = Assert.Single(checker.Compilators!);
        Assert.Equal("Kotlin 2.4 (OpenJDK 27)", compilator.DisplayName);
        Assert.Null(compilator.Error);
        var request = Assert.Single(runner.Requests);
        Assert.False(request.ClearWorkdir);
        Assert.False(request.ReadOnly);
        Assert.True(request.Stdin.IsEmpty);
        Assert.Empty(request.Files);
    }

    [Theory]
    [InlineData(RunnerStatus.Completed, 1)]
    [InlineData(RunnerStatus.Error, 1)]
    [InlineData(RunnerStatus.UserTimeLimit, 0)]
    public async Task ParseableOutputDoesNotHideVersionCommandFailure(RunnerStatus status, int exitCode)
    {
        var runner = new TestRunner { Handler = (_, _) => Task.FromResult(Completed("1", status: status, exitCode: exitCode)) };
        var checker = CreateChecker(runner);

        await checker.LoadCompilators(TestContext.Current.CancellationToken);

        Assert.NotNull(Assert.Single(checker.Compilators!).Error);
        Assert.Equal(CheckerResultStatus.InternalError, checker.SelfTestingResults.Single(result => result.Name == "Version").Result);
    }

    [Fact]
    public async Task SelfTestsAreSelectedByLanguageAndOptionalCompilerNames()
    {
        var checker = new CheckerService(new TestRunner(),
            [new TestConfiguration { Name = "A" }, new TestConfiguration { Name = "B" },
                new TestConfiguration { Name = "C", Language = "C++" }],
            [new TestSelfCheck { Name = "Common" },
                new TestSelfCheck { Name = "OnlyA", CompilatorNames = ["A"] },
                new TestSelfCheck { Name = "Cpp", Language = "C++" }], []);

        Assert.True(await checker.RunSelfTests(TestContext.Current.CancellationToken));

        var programs = checker.SelfTestingResults.Where(result => result.Name != "Version").ToArray();
        Assert.Equal(["Common", "OnlyA"], programs.Where(result => result.Compilator.Configuration.Name == "A").Select(result => result.Name));
        Assert.Equal(["Common"], programs.Where(result => result.Compilator.Configuration.Name == "B").Select(result => result.Name));
        Assert.Equal(["Cpp"], programs.Where(result => result.Compilator.Configuration.Name == "C").Select(result => result.Name));
    }

    [Fact]
    public async Task ExpectedCompilationErrorIsSuccessfulSelfTest()
    {
        var runner = new TestRunner { CompilationFails = true };
        var checker = new CheckerService(runner, [new TestConfiguration()],
            [new TestSelfCheck { ExpectedResult = CheckerResultStatus.CompilationError }], []);

        Assert.True(await checker.RunSelfTests(TestContext.Current.CancellationToken));

        Assert.Equal(CheckerSelfTestingStatus.Success, checker.SelfTestingStatus);
        Assert.Equal(0, runner.TestsRun);
        Assert.Equal(CheckerResultStatus.CompilationError,
            checker.SelfTestingResults.Single(result => result.Name == nameof(TestSelfCheck)).Result);
    }

    [Fact]
    public async Task FailedSelfTestDoesNotPreventOtherCompilersFromBeingTested()
    {
        var runner = new TestRunner { FailureTests = [1] };
        var checker = new CheckerService(runner, [new TestConfiguration { Name = "A" }, new TestConfiguration { Name = "B" }],
            [new TestSelfCheck()], []);

        Assert.False(await checker.RunSelfTests(TestContext.Current.CancellationToken));

        Assert.Equal(2, runner.TestsRun);
        Assert.Equal(CheckerResultStatus.WrongAnswer,
            checker.SelfTestingResults.Single(result => result.Name == nameof(TestSelfCheck) && result.Compilator.Configuration.Name == "A").Result);
        Assert.Equal(CheckerResultStatus.Accepted,
            checker.SelfTestingResults.Single(result => result.Name == nameof(TestSelfCheck) && result.Compilator.Configuration.Name == "B").Result);
    }

    [Fact]
    public async Task NoAvailableCompilersCannotPassSelfTesting()
    {
        var runner = new TestRunner();
        var checker = new CheckerService(runner, [], [new TestSelfCheck()], []);

        Assert.False(await checker.RunSelfTests(TestContext.Current.CancellationToken));

        Assert.Equal(CheckerSelfTestingStatus.Failed, checker.SelfTestingStatus);
        Assert.Empty(checker.SelfTestingResults);
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("run")]
    public async Task CancellationLeavesPendingResultsAndAllowsSelfTestingAgain(string command)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bool cancel = true;
        var runner = new TestRunner
        {
            Handler = (request, token) =>
            {
                if (request.Command == command && cancel)
                {
                    cancel = false;
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return Task.FromResult(Completed(request.Command == "version" ? "1" : "ok"));
            }
        };
        var checker = new CheckerService(runner, [new TestConfiguration()], [new TestSelfCheck()], []);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.RunSelfTests(cancellation.Token));
        Assert.Null(checker.SelfTestingResults.Single(result => result.Name == nameof(TestSelfCheck)).Result);
        Assert.True(await checker.RunSelfTests(TestContext.Current.CancellationToken));

        Assert.Equal(CheckerSelfTestingStatus.Success, checker.SelfTestingStatus);
        Assert.All(checker.SelfTestingResults, result => Assert.Equal(result.ExpectedResult, result.Result));
    }
}

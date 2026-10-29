using System.Net;
using System.Text;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Olymp.Checker;
using Olymp.Checker.Checkers;
using Olymp.Checker.Services;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site.Services.Checker;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.Site.Checkers;

internal static class CheckerTestData
{
    public static async Task<Submission[]> AddSubmissionsAsync(SiteFactory factory, string prefix, int[] statuses)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var now = DateTimeOffset.UtcNow;
        var contest = TestEntities.CreateContest("Checker tests", now.AddHours(-1), now.AddHours(1));
        contest.Abbr = prefix + Guid.NewGuid().ToString("N");
        var problem = TestEntities.CreateProblem(contest);
        problem.Checker = null!;
        problem.CheckerId = factory.Data.Problem.CheckerId;
        int[] scores = [0, 20, 30, 50];
        for (int i = 0; i < scores.Length; i++)
            problem.Tests.Add(new Test
            {
                Problem = problem,
                Number = i + 1,
                Input = Encoding.UTF8.GetBytes((i + 1).ToString()),
                Output = "ok"u8.ToArray(),
                IsActive = true,
                IsOpen = i == 0,
                Score = scores[i]
            });

        var compilator = TestEntities.CreateCompilator();
        compilator.Name = "test-" + Guid.NewGuid().ToString("N");
        compilator.Language = "C";
        compilator.SourceExtension = ".c";
        compilator.CommandLine = "compile";
        var submissions = statuses.Select(status =>
        {
            var submission = TestEntities.CreateSubmission(factory.Data.Competitor, problem, compilator, now, "test"u8.ToArray());
            submission.User = null!;
            submission.StatusCode = status;
            submission.LastModification = now;
            submission.Score = status == 2 ? 100 : 25;
            submission.ScoresByTest = "25;0";
            submission.CheckResults.Add(new CheckResult
            {
                Submission = submission,
                StatusCode = status,
                CheckTime = now,
                Log = "Old check"
            });
            return submission;
        }).ToArray();
        context.Submissions.AddRange(submissions);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return submissions;
    }

    public static async Task<CheckerService> CreateCheckerAsync(TestRunner runner, string compilatorName, bool slow = false)
    {
        var configuration = new TestConfiguration { Name = compilatorName, UseSlowTimeLimit = slow };
        var checker = new CheckerService(runner, [configuration], [new SelfTest()], [new LegacyChecker()]);
        await checker.LoadCompilators(TestContext.Current.CancellationToken);
        return checker;
    }

    public static WebApplicationFactory<Program> WithManager(SiteFactory factory, PageManager manager) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICheckerManager>();
            services.AddSingleton<ICheckerManager>(manager);
        }));

    public static WebApplicationFactory<Program> WithRunnerConfiguration(SiteFactory factory, TestConfiguration? configuration = null) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICompilatorConfiguration>();
            services.AddSingleton<ICompilatorConfiguration>(configuration ?? new TestConfiguration());
            services.RemoveAll<ICheckerTest>();
            services.AddSingleton<ICheckerTest>(new SelfTest());
        }));

    public static HttpClient CreateClient(WebApplicationFactory<Program> site) =>
        site.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

    public static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> site, string username)
    {
        var client = CreateClient(site);
        using var login = await SiteFactory.PostFormAsync(client, "/Identity/Account/Login", new()
        {
            ["Input.Username"] = username,
            ["Input.Password"] = SiteData.Password
        });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        return client;
    }

    public sealed class TestConfiguration : ICompilatorConfiguration
    {
        public string Name { get; init; } = "test";
        public string DisplayName { get; init; } = "Test compiler";
        public string Language => "C";
        public string ConfigName => "test";
        public string SourceExtension => ".c";
        public string CompileCommand => "compile";
        public bool UseSlowTimeLimit { get; init; }
        public int MemoryOverheadMb => 0;
        public string GetSourceFileName(string name) => name + ".c";
        public string GetCompileCommand(string name) => "compile";
        public string RunCommand(string name, int memoryMb) => "run";
        public string GetVersionCommand() => "version";
        public CompilatorInfo ParseInfo(string output) => new(this, DisplayName, "private compiler version");
    }

    public sealed class SelfTest : ICheckerTest
    {
        public string Language => "C";
        public string Source => "source";
        public string Output => "ok";
        public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    }

    public sealed class TestRunner : IRunnerService
    {
        public const string CompilerError = "42.c:1: error: expected ';'\n42.c:2: error: unknown type 'Compilation:'";
        public Guid Id { get; } = Guid.NewGuid();
        public string Name => "Test runner";
        public DateTimeOffset ConnectingTime { get; } = DateTimeOffset.Now;
        public IReadOnlySet<string> SupportedEnvs { get; } = new HashSet<string> { "test" };
        public List<RunnerTask> Requests { get; } = [];
        public int[] FailureTests { get; init; } = [];
        public bool CompilationFails { get; init; }
        public RunnerStatus VersionStatus { get; init; } = RunnerStatus.Completed;
        public Func<CancellationToken, Task>? BeforeCompile { get; set; }
        public byte[]? CompilationSource { get; private set; }
        public int TestsRun { get; private set; }

        public async Task<RunnerResult> Run(RunnerTask request, CancellationToken token = default)
        {
            Requests.Add(request);
            bool compile = request.Command.StartsWith("compile", StringComparison.Ordinal);
            if (compile)
            {
                if (BeforeCompile is not null)
                    await BeforeCompile(token);
                CompilationSource = Assert.Single(request.Files).Content.ToArray();
            }
            bool run = request.Command == "run";
            var output = run ? FailureTests.Contains(++TestsRun) ? "wrong" : "ok" : "1";
            var status = request.Command == "version" ? VersionStatus
                : compile && CompilationFails ? RunnerStatus.Error : RunnerStatus.Completed;
            return new(status, compile && CompilationFails ? 1 : 0, Encoding.UTF8.GetBytes(output),
                compile && CompilationFails ? Encoding.UTF8.GetBytes(CompilerError) : ReadOnlyMemory<byte>.Empty,
                new(TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0));
        }
    }

    public sealed class PageManager(ICheckerService? checker = null) : ICheckerManager
    {
        public Dictionary<Guid, ICheckerService> Connected { get; } = checker is null ? [] : new() { [checker.Id] = checker };
        public IReadOnlyDictionary<Guid, ICheckerService> Checkers => Connected;
        public void Register(IRunnerService runner, CancellationToken token) => throw new NotSupportedException();
        public void Unregister(IRunnerService runner) => Connected.Remove(runner.Id);
        public Task<IDisposable> EnterAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<bool> TryCheckFromDB(ICheckerService checker, CancellationToken token) => throw new NotSupportedException();
        public Task<int> AddCompilators(Guid checkerId, CancellationToken token) => throw new NotSupportedException();
        public Task<int> DisableCompilatorsWithoutRunner(CancellationToken token) => throw new NotSupportedException();
    }

    public sealed class RunnerRegistration : IAsyncDisposable
    {
        private readonly ICheckerManager _manager;
        private readonly TestRunner _runner;
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        private RunnerRegistration(ICheckerManager manager, TestRunner runner)
        {
            _manager = manager;
            _runner = runner;
        }

        public ICheckerService Checker => _manager.Checkers[_runner.Id];

        public static async Task<RunnerRegistration> ConnectAsync(ICheckerManager manager, TestRunner runner)
        {
            var connection = new RunnerRegistration(manager, runner);
            try
            {
                manager.Register(runner, connection._stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                while (connection.Checker.SelfTestingStatus is CheckerSelfTestingStatus.Unknown or CheckerSelfTestingStatus.InProcess)
                    await Task.Delay(10, timeout.Token);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _manager.Unregister(_runner);
            _stop.Dispose();
        }
    }
}

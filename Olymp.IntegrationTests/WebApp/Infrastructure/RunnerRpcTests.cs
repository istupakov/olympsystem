using System.Net;
using System.Net.Http.Headers;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using Grpc.Core;
using Grpc.Net.Client;

using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Olymp.Checker;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.Services.Checkers;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.RunnerProtocol;
using Olymp.Tests.Shared;
using Olymp.WebApp.Services.Checker;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Infrastructure;

public class RunnerRpcTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Fact]
    public async Task NewApplicationChecksQueuedSubmissionAndRetainsSourceIpAndHistory()
    {
        var user = await factory.AddUserAsync();
        var now = DateTimeOffset.UtcNow;
        var contest = TestEntities.CreateContest("New application queue", now.AddHours(-1), now.AddHours(1));
        var problem = TestEntities.CreateProblem(contest);
        problem.Tests.Add(new()
        {
            Problem = problem,
            Number = 1,
            Input = "1"u8.ToArray(),
            Output = "ok"u8.ToArray(),
            IsActive = true,
            Score = 100
        });
        var compiler = TestEntities.CreateCompilator();
        compiler.Language = "C";
        compiler.SourceExtension = ".c";
        compiler.CommandLine = "compile";
        var submission = TestEntities.CreateSubmission(user, problem, compiler, now, "source"u8.ToArray());
        submission.User = null!;
        submission.CommiterInfo = "203.0.113.42";
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<OlympContext>();
            db.Submissions.Add(submission);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var runner = new CheckerTestData.TestRunner();
        var checker = await CheckerTestData.CreateCheckerAsync(runner, compiler.Name);
        Assert.True(await factory.Services.GetRequiredService<ICheckerManager>().TryCheckFromDB(checker, TestContext.Current.CancellationToken));
        await using var verification = factory.Services.CreateAsyncScope();
        var saved = await verification.ServiceProvider.GetRequiredService<OlympContext>().Submissions.Include(item => item.CheckResults)
            .SingleAsync(item => item.Id == submission.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, saved.StatusCode);
        Assert.Equal(100, saved.Score);
        Assert.Equal("203.0.113.42", saved.CommiterInfo);
        Assert.Equal("source"u8.ToArray(), saved.SourceCode);
        Assert.Equal(2, Assert.Single(saved.CheckResults).StatusCode);
        Assert.Equal(1, runner.TestsRun);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-key")]
    public async Task RunnerRequiresConfiguredApiKeyInsteadOfRedirectingToLogin(string? apiKey)
    {
        using var client = factory.CreateSiteClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/runner.Runner/Connect")
        {
            Version = HttpVersion.Version20,
            Content = new ByteArrayContent([])
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        if (apiKey is not null)
            request.Headers.TryAddWithoutValidation("Authorization", apiKey);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task AuthorizedRunnerRegistersExchangesCommandsAndUnregistersOnDisconnect()
    {
        var manager = new ConnectedRunnerManager();
        await using var server = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ApiKeys:0"] = "test-runner-key" }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICheckerManager>();
                services.AddSingleton<ICheckerManager>(manager);
            });
        });
        using var channel = GrpcChannel.ForAddress("https://localhost", new GrpcChannelOptions
        {
            HttpHandler = server.Server.CreateHandler()
        });
        var method = new Method<CommandResponse, CommandRequest>(MethodType.DuplexStreaming, "runner.Runner", "Connect",
            Marshallers.Create(message => message.ToByteArray(), CommandResponse.Parser.ParseFrom),
            Marshallers.Create(message => message.ToByteArray(), CommandRequest.Parser.ParseFrom));
        var id = Guid.NewGuid();
        var headers = new Metadata
        {
            { "authorization", "test-runner-key" }, { "runner-id", id.ToString() },
            { "runner-name", "RPC test runner" }, { "runner-envs", "test;second" }
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var call = channel.CreateCallInvoker().AsyncDuplexStreamingCall(method, null, new(headers, cancellationToken: timeout.Token));
        var nextCommand = call.ResponseStream.MoveNext(timeout.Token);
        var runner = await manager.Connected.Task.WaitAsync(timeout.Token);
        Assert.Equal(id, runner.Id);
        Assert.Equal("RPC test runner", runner.Name);
        Assert.True(runner.SupportedEnvs.SetEquals(["test", "second"]));
        var pending = runner.Run(new("compile", "stdin"u8.ToArray(), true, true, ["test"],
            [new("source.c", "source"u8.ToArray())], new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 128, 32, 16)), timeout.Token);
        Assert.True(await nextCommand);
        var command = call.ResponseStream.Current;
        Assert.Equal("compile", command.Command);
        Assert.Equal("stdin", command.Stdin.ToStringUtf8());
        Assert.True(command.ClearWorkdir);
        Assert.True(command.ReadOnly);
        Assert.Equal("test", Assert.Single(command.EnvFiles));
        Assert.Equal("source.c", Assert.Single(command.Files).Filename);
        Assert.Equal("source", command.Files[0].Content.ToStringUtf8());
        Assert.Equal(128UL, command.ResourceLimits.MemoryBytes);
        await call.RequestStream.WriteAsync(new CommandResponse
        {
            Status = CommandStatus.Completed,
            ExitCode = 7,
            Stdout = ByteString.CopyFromUtf8("result"),
            Stderr = ByteString.CopyFromUtf8("diagnostic"),
            ResourceConsumption = new()
            {
                UserTime = Duration.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
                TotalTime = Duration.FromTimeSpan(TimeSpan.FromMilliseconds(200)),
                MemoryBytes = 64,
                StdoutBytes = 6,
                StderrBytes = 10
            }
        }, timeout.Token);
        var result = await pending.WaitAsync(timeout.Token);
        Assert.Equal(RunnerStatus.Completed, result.Status);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("result"u8.ToArray(), result.Stdout.ToArray());
        Assert.Equal("diagnostic"u8.ToArray(), result.Stderr.ToArray());
        Assert.Equal(TimeSpan.FromMilliseconds(100), result.ResourceConsumption.UserTime);
        call.Dispose();
        Assert.Equal(runner.Id, await manager.Disconnected.Task.WaitAsync(timeout.Token));
    }

    private sealed class ConnectedRunnerManager : ICheckerManager
    {
        public TaskCompletionSource<IRunnerService> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Guid> Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyDictionary<Guid, ICheckerService> Checkers => new Dictionary<Guid, ICheckerService>();
        public void Register(IRunnerService runner, CancellationToken token) => Connected.TrySetResult(runner);
        public void Unregister(IRunnerService runner) => Disconnected.TrySetResult(runner.Id);
        public Task<IDisposable> EnterAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<bool> TryCheckFromDB(ICheckerService checker, CancellationToken token) => throw new NotSupportedException();
        public Task<int> AddCompilatorAsync(Guid runnerId, string name, string description, CompilerConfigurationKey? expected = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task SetCompilatorActiveAsync(int id, bool active, CancellationToken token = default) => throw new NotSupportedException();
        public Task<int?> QueueRetestAsync(IQueryable<Submission> selection, IReadOnlyCollection<int> confirmedAccepted, CancellationToken token = default) => throw new NotSupportedException();
    }
}

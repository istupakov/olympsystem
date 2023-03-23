using System.Threading.Channels;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Olymp.Site.Protos;

namespace Olymp.Site.Services.Checker;

public record RunnerFile(string Filename, ReadOnlyMemory<byte> Content);

public record RunnerResources(TimeSpan UserTime, TimeSpan TotalTime,
    ulong Memory, ulong Stdout, ulong Stderr);

public record RunnerTask(string Command, ReadOnlyMemory<byte> Stdin,
    bool ClearWorkdir, bool ReadOnly,
    ICollection<string> EnvFiles, ICollection<RunnerFile> Files,
    RunnerResources ResourceLimits);

public record RunnerResult(CommandStatus Status, int ExitCode,
    ReadOnlyMemory<byte> Stdout, ReadOnlyMemory<byte> Stderr,
    RunnerResources ResourceConsumption);

public interface IRunnerService
{
    Guid Id { get; }
    string Name { get; }
    DateTimeOffset ConnectingTime { get; }
    IReadOnlySet<string> SupportedEnvs { get; }

    Task<RunnerResult> Run(RunnerTask request, CancellationToken token = default);
}

public class RunnerService : Runner.RunnerBase, IRunnerService
{
    private readonly ILogger _logger;
    private readonly ICheckerManager _manager;
    private readonly Channel<CommandRequest> _request = Channel.CreateBounded<CommandRequest>(1);
    private readonly Channel<CommandResponse> _response = Channel.CreateBounded<CommandResponse>(1);
    private readonly HashSet<string> _supportedEnvs = new();

    public Guid Id { get; private set; } = Guid.Empty;
    public string Name { get; private set; } = null!;
    public DateTimeOffset ConnectingTime { get; } = DateTimeOffset.Now;
    public IReadOnlySet<string> SupportedEnvs => _supportedEnvs;

    public RunnerService(ILogger<RunnerService> logger, ICheckerManager manager)
    {
        _logger = logger;
        _manager = manager;
    }

    public async Task<RunnerResult> Run(RunnerTask task, CancellationToken token = default)
    {
        _logger.LogDebug("Trying run command: {command}", task);

        var request = new CommandRequest()
        {
            Command = task.Command,
            Stdin = ByteString.CopyFrom(task.Stdin.Span),
            ClearWorkdir = task.ClearWorkdir,
            ReadOnly = task.ReadOnly,
            ResourceLimits = new()
            {
                UserTime = Duration.FromTimeSpan(task.ResourceLimits.UserTime),
                TotalTime = Duration.FromTimeSpan(task.ResourceLimits.TotalTime),
                MemoryBytes = task.ResourceLimits.Memory,
                StdoutBytes = task.ResourceLimits.Stdout,
                StderrBytes = task.ResourceLimits.Stderr
            }
        };

        foreach (var envFile in task.EnvFiles)
            request.EnvFiles.Add(envFile);

        foreach (var (filename, content) in task.Files)
            request.Files.Add(new CommandFile { Filename = filename, Content = ByteString.CopyFrom(content.Span) });

        await _request.Writer.WriteAsync(request, token);
        var response = await _response.Reader.ReadAsync(CancellationToken.None);

        var result = new RunnerResult(response.Status, response.ExitCode, response.Stdout.Memory, response.Stderr.Memory,
            new RunnerResources(response.ResourceConsumption.UserTime.ToTimeSpan(),
                response.ResourceConsumption.TotalTime.ToTimeSpan(),
                response.ResourceConsumption.MemoryBytes,
                response.ResourceConsumption.StdoutBytes,
                response.ResourceConsumption.StderrBytes));

        _logger.LogDebug("Received command result: {result}", result);
        return result;
    }

    public override async Task Connect(IAsyncStreamReader<CommandResponse> requestStream, IServerStreamWriter<CommandRequest> responseStream, ServerCallContext context)
    {
        try
        {
            Id = Guid.Parse(context.RequestHeaders.Single(x => x.Key == "runner-id").Value);
            Name = context.RequestHeaders.Single(x => x.Key == "runner-name").Value;
            var supportedEnvs = context.RequestHeaders.Single(x => x.Key == "runner-envs").Value;
            foreach (var env in supportedEnvs.Split(';'))
                _supportedEnvs.Add(env);

            _logger.LogDebug("Connecting runner: runner-id {id}, runner-name {name}, runner-envs {supportedEnvs}",
                Id, Name, supportedEnvs);

            _manager.Register(this, context.CancellationToken);

            await foreach (var command in _request.Reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(command, context.CancellationToken);
                if (!await requestStream.MoveNext())
                    throw new Exception("Runner disconnected");

                await _response.Writer.WriteAsync(requestStream.Current, context.CancellationToken);
            }
        }
        finally
        {
            _manager.Unregister(this);
            _request.Writer.TryComplete();
            _response.Writer.TryComplete();
        }
    }
}

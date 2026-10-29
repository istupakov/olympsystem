using System.Threading.Channels;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Olymp.Checker;
using Olymp.Site.Protos;

namespace Olymp.Site.Services.Checker;

public sealed class RunnerService(ILogger<RunnerService> logger, ICheckerManager manager) : Runner.RunnerBase, IRunnerService
{
    private readonly ILogger _logger = logger;
    private readonly ICheckerManager _manager = manager;
    private readonly Channel<CommandRequest> _request = Channel.CreateBounded<CommandRequest>(1);
    private readonly Channel<CommandResponse> _response = Channel.CreateBounded<CommandResponse>(1);
    private readonly HashSet<string> _supportedEnvs = [];

    public Guid Id { get; private set; } = Guid.Empty;
    public string Name { get; private set; } = null!;
    public DateTimeOffset ConnectingTime { get; } = DateTimeOffset.Now;
    public IReadOnlySet<string> SupportedEnvs => _supportedEnvs;

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

        var status = response.Status switch
        {
            CommandStatus.Completed => RunnerStatus.Completed,
            CommandStatus.Error => RunnerStatus.Error,
            CommandStatus.UserTimeLimit => RunnerStatus.UserTimeLimit,
            CommandStatus.TotalTimeLimit => RunnerStatus.TotalTimeLimit,
            CommandStatus.MemoryLimit => RunnerStatus.MemoryLimit,
            CommandStatus.StdoutLimit => RunnerStatus.StdoutLimit,
            CommandStatus.StderrLimit => RunnerStatus.StderrLimit,
            _ => RunnerStatus.Unspecified
        };
        var result = new RunnerResult(status, response.ExitCode, response.Stdout.Memory, response.Stderr.Memory,
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

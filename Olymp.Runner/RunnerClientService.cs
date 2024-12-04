using System.Diagnostics;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Microsoft.Extensions.Options;

using Olymp.Site.Protos;

namespace Olymp.Runner;

public class RunnerClientService : BackgroundService
{
    private readonly Site.Protos.Runner.RunnerClient _runnerClient;
    private readonly ILogger<RunnerClientService> _logger;
    private readonly IRestrictedProcessFactory _processFactory;
    private readonly string _workDirectory;
    private readonly string _envFilesDirectory;
    private readonly string? _readOnlyUser;
    private readonly Guid _id = Guid.NewGuid();

    public RunnerClientService(Site.Protos.Runner.RunnerClient runnerClient,
        IOptions<RunnerClientConfig> options,
        ILogger<RunnerClientService> logger,
        IRestrictedProcessFactory processFactory)
    {
        _runnerClient = runnerClient;
        _workDirectory = Path.GetFullPath(options.Value.WorkingDirectory
            ?? throw new Exception($"WorkingDirectory must be set"));
        _envFilesDirectory = Path.GetFullPath(options.Value.EnvFilesDirectory
            ?? throw new Exception($"EnvFilesDirectory must be set"));
        _readOnlyUser = options.Value.ReadOnlyUser;
        _logger = logger;
        _processFactory = processFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_workDirectory);
        await LogWhoami(null, stoppingToken);
        await LogWhoami(_readOnlyUser, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var supportedEnvs = Directory.EnumerateFiles(_envFilesDirectory).Select(Path.GetFileNameWithoutExtension);

                var metadata = new Metadata
                {
                    { "runner-id", _id.ToString() },
                    { "runner-name", "Olymp runner v2.1 beta" },
                    { "runner-envs", string.Join(';', supportedEnvs) }
                };

                using var connect = _runnerClient.Connect(metadata, cancellationToken: stoppingToken);
                await foreach (var request in connect.ResponseStream.ReadAllAsync(stoppingToken))
                {
                    _logger.LogDebug("Received command: {command}", request.Command);
                    var response = await Run(request, stoppingToken);

                    _logger.LogInformation("""
                        Command {command}:
                        status {status}, exit code {exitCode},
                        user time {userTime}, total time {totalTime},
                        peak memory {peakMemory} KB,  stdout {stdout}, stderr {stderr}
                        """,
                        request.Command, response.Status, response.ExitCode, response.ResourceConsumption.UserTime,
                        response.ResourceConsumption.TotalTime, response.ResourceConsumption.MemoryBytes / 1024,
                        response.ResourceConsumption.StdoutBytes, response.ResourceConsumption.StderrBytes);

                    await connect.RequestStream.WriteAsync(response, stoppingToken);
                }
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "IO Error");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (RpcException ex)
            {
                _logger.LogError(ex, "Rpc Error");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task ClearWorkDir(CancellationToken token)
    {
        Directory.CreateDirectory(_workDirectory);
        foreach (var file in Directory.EnumerateFiles(_workDirectory))
        {
        delete:
            try
            {
                token.ThrowIfCancellationRequested();
                File.Delete(file);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Error clearing work directory");
                await Task.Delay(1000, token);
                goto delete;
            }
        }
    }

    private async Task LogWhoami(string? user, CancellationToken token)
    {
        using var process = _processFactory.Create("whoami", _workDirectory,
            user, CreateEnviromentVariables(Enumerable.Empty<string>()),
            2, ProcessPriorityClass.Normal, TimeSpan.FromSeconds(1), 128_000_000);

        await process.Process.WaitForExitAsync(token);

        _logger.LogInformation("""
            whoami as: {username}
            exitcode: {exitcode}
            stdout: {stdout}
            stderr: {stderr}
            """,
            user, process.Process.ExitCode,
            await process.Process.StandardOutput.ReadToEndAsync(token),
            await process.Process.StandardError.ReadToEndAsync(token));
    }

    private async Task CreateFile(string filename, ReadOnlyMemory<byte> content, CancellationToken token)
    {
        using var file = File.Create(Path.Combine(_workDirectory, filename));
        await file.WriteAsync(content, token);
    }

    private Dictionary<string, string> CreateEnviromentVariables(IEnumerable<string> envFiles)
    {
        var envs = new Dictionary<string, string>();
        foreach (string envFile in new[] { "base" }.Concat(envFiles))
        {
            string path = Path.Combine(_envFilesDirectory, Path.ChangeExtension(envFile, ".env"));
            foreach (string line in File.ReadLines(path))
            {
                if (line.Split('=') is [string name, string value])
                {
                    envs[name.Trim()] = value.Trim();
                }
            }
        }

        return envs;
    }

    public async Task<CommandResponse> Run(CommandRequest request, CancellationToken token)
    {
        if (request.ClearWorkdir)
        {
            _logger.LogDebug("Clear workdir");
            await ClearWorkDir(token);
        }

        foreach (var file in request.Files)
        {
            _logger.LogDebug("Create file {filename}", file.Filename);
            await CreateFile(file.Filename, file.Content.Memory, token);
        }

        var envs = CreateEnviromentVariables(request.EnvFiles);

        var userTimeLimit = request.ResourceLimits.UserTime.ToTimeSpan();
        nuint memoryLimit = new(request.ResourceLimits.MemoryBytes);
        using var process = _processFactory.Create(request.Command, _workDirectory,
            request.ReadOnly ? _readOnlyUser : null, envs,
            5, ProcessPriorityClass.Normal, userTimeLimit, memoryLimit);

        var processInputTask = Task.Run(async () =>
        {
            try
            {
                await process.Process.StandardInput.BaseStream.WriteAsync(request.Stdin.Memory, token);
                process.Process.StandardInput.Close();
            }
            catch (IOException) { }
        }, token);
        async Task<(Memory<byte> memory, bool limit)> ReadToBuffer(Stream stream, int size)
        {
            var buffer = new byte[size];
            int cur = 0;
            while (await stream.ReadAsync(buffer.AsMemory(cur..), token) is int len and > 0)
            {
                cur += len;
                if (cur == size)
                    return (buffer, true);
            }
            return (buffer.AsMemory(..cur), false);
        }
        var processOutputTask = Task.Run(() =>
            ReadToBuffer(process.Process.StandardOutput.BaseStream,
            (int)request.ResourceLimits.StdoutBytes), token);
        var processErrorTask = Task.Run(() =>
            ReadToBuffer(process.Process.StandardError.BaseStream,
            (int)request.ResourceLimits.StderrBytes), token);

        var totalTimeLimit = request.ResourceLimits.TotalTime.ToTimeSpan();
        while (!process.Process.HasExited)
        {
            if (DateTime.Now - process.Process.StartTime > totalTimeLimit)
                process.Terminate(0xffff);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                await process.Process.WaitForExitAsync(cts.Token);
            }
            catch (TaskCanceledException) { }
        }

        await Task.WhenAll(processInputTask, processOutputTask, processErrorTask);

        var processTotalTime = process.Process.ExitTime - process.Process.StartTime;
        return new CommandResponse
        {
            Status = (process.Process.ExitCode,
                process.TotalUserTime > userTimeLimit, processTotalTime > totalTimeLimit,
                process.PeakJobMemoryUsed > memoryLimit,
                processOutputTask.Result.limit, processErrorTask.Result.limit) switch
            {
                (_, _, _, true, _, _) => CommandStatus.MemoryLimit,
                (_, _, _, _, true, _) => CommandStatus.StdoutLimit,
                (_, _, _, _, _, true) => CommandStatus.StderrLimit,
                (_, true, _, _, _, _) => CommandStatus.UserTimeLimit,
                (_, _, true, _, _, _) => CommandStatus.TotalTimeLimit,
                (not 0, _, _, _, _, _) => CommandStatus.Error,
                (0, _, _, _, _, _) => CommandStatus.Completed,
            },
            Stdout = ByteString.CopyFrom(processOutputTask.Result.memory.Span),
            Stderr = ByteString.CopyFrom(processErrorTask.Result.memory.Span),
            ExitCode = process.Process.ExitCode,
            ResourceConsumption = new()
            {
                UserTime = Duration.FromTimeSpan(process.TotalUserTime),
                TotalTime = Duration.FromTimeSpan(processTotalTime),
                MemoryBytes = process.PeakJobMemoryUsed,
                StdoutBytes = (ulong)processOutputTask.Result.memory.Length,
                StderrBytes = (ulong)processErrorTask.Result.memory.Length
            }
        };
    }
}

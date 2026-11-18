using Grpc.Core;

using Microsoft.Extensions.Options;

using Olymp.RunnerProtocol;

namespace Olymp.Runner;

public class RunnerClientService(RunnerProtocol.Runner.RunnerClient runnerClient,
    IOptions<RunnerClientConfig> options,
    ILogger<RunnerClientService> logger,
    CommandExecutor commandExecutor) : BackgroundService
{
    private readonly string _workDirectory = Path.GetFullPath(options.Value.WorkingDirectory);
    private readonly string _envFilesDirectory = Path.GetFullPath(options.Value.EnvFilesDirectory);
    private readonly string? _readOnlyUser = options.Value.ReadOnlyUser;
    private readonly Guid _id = Guid.NewGuid();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_workDirectory);
        await commandExecutor.LogWhoami(null, _workDirectory, CreateEnvironmentVariables([]), stoppingToken);
        await commandExecutor.LogWhoami(_readOnlyUser, _workDirectory, CreateEnvironmentVariables([]), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var supportedEnvs = Directory.EnumerateFiles(_envFilesDirectory, "*.env")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(x => !string.Equals(x, "base", StringComparison.OrdinalIgnoreCase));

                var metadata = new Metadata
                {
                    { "runner-id", _id.ToString() },
                    { "runner-name", "Olymp runner v2.2 beta" },
                    { "runner-envs", string.Join(';', supportedEnvs) }
                };

                using var connect = runnerClient.Connect(metadata, cancellationToken: stoppingToken);
                await foreach (var request in connect.ResponseStream.ReadAllAsync(stoppingToken))
                {
                    logger.LogDebug("Received command: {command}", request.Command);
                    var response = await Run(request, stoppingToken);

                    logger.LogInformation("""
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
                logger.LogError(ex, "IO Error");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (RpcException ex)
            {
                logger.LogError(ex, "Rpc Error");
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
                logger.LogError(ex, "Error clearing work directory");
                await Task.Delay(1000, token);
                goto delete;
            }
        }
    }

    private async Task CreateFile(string filename, ReadOnlyMemory<byte> content, CancellationToken token)
    {
        using var file = File.Create(Path.Combine(_workDirectory, filename));
        await file.WriteAsync(content, token);
    }

    private Dictionary<string, string> CreateEnvironmentVariables(IEnumerable<string> envFiles)
    {
        var envs = new Dictionary<string, string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string envFile in (IEnumerable<string>)["base", .. envFiles])
        {
            string path = Path.Combine(_envFilesDirectory, Path.ChangeExtension(envFile, ".env"));
            foreach (string line in File.ReadLines(path))
            {
                if (line.Split('=', 2) is [string name, string value] && !string.IsNullOrWhiteSpace(name))
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
            logger.LogDebug("Clear workdir");
            await ClearWorkDir(token);
        }

        foreach (var file in request.Files)
        {
            logger.LogDebug("Create file {filename}", file.Filename);
            await CreateFile(file.Filename, file.Content.Memory, token);
        }

        var envs = CreateEnvironmentVariables(request.EnvFiles);

        return await commandExecutor.Run(request, _workDirectory, request.ReadOnly ? _readOnlyUser : null, envs, token);
    }
}

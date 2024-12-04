namespace Olymp.Runner;

public class RunnerClientConfig
{
    public const string Section = "RunnerClient";
    public string? WorkingDirectory { get; init; }
    public string? EnvFilesDirectory { get; init; }
    public string? ReadOnlyUser { get; init; }
}

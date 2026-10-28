using System.ComponentModel.DataAnnotations;

namespace Olymp.Runner;

public class RunnerClientConfig
{
    public const string Section = "RunnerClient";

    [Required]
    public required string WorkingDirectory { get; init; }

    [Required]
    public required string EnvFilesDirectory { get; init; }

    public string? ReadOnlyUser { get; init; }
}

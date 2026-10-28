using System.ComponentModel.DataAnnotations;

namespace Olymp.Runner;

public class RunnerConnectionConfig
{
    [Required]
    public required Uri RunnerServerAddress { get; init; }

    [Required]
    public required string ApiKey { get; init; }
}

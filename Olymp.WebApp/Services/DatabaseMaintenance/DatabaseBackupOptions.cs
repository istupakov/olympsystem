using System.ComponentModel.DataAnnotations;

namespace Olymp.WebApp.Services.DatabaseMaintenance;

public sealed class DatabaseBackupOptions
{
    public const string SectionName = "DatabaseBackups";

    [Required]
    public required string LocalPath { get; init; }

    [Required]
    public required string SqlPath { get; init; }
}

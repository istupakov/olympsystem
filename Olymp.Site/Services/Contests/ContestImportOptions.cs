namespace Olymp.Site.Services.Contests;

public sealed class ContestImportOptions
{
    public long MaxPdfBytes { get; init; } = 20 * 1024 * 1024;
}

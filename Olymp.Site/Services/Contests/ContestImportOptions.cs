namespace Olymp.Site.Services.Contests;

public sealed class ContestImportOptions
{
    public long MaxZipBytes { get; init; } = 100 * 1024 * 1024;
    public long MaxExpandedBytes { get; init; } = 512 * 1024 * 1024;
    public int MaxEntries { get; init; } = 10000;
    public long MaxPdfBytes { get; init; } = 20 * 1024 * 1024;
}

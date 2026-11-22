using System.IO.Compression;
using System.Text;

using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Contests;

internal sealed class ImportArchive(MemoryStream stream, ZipArchive archive, Dictionary<string, ZipArchiveEntry> files) : IDisposable
{
    public IReadOnlyDictionary<string, ZipArchiveEntry> Files => files;

    public static async Task<ImportArchive> ReadAsync(Stream source, ContestImportOptions options, CancellationToken token)
    {
        var bounded = new MemoryStream();
        try
        {
            var buffer = new byte[81920];
            int count;
            while ((count = await source.ReadAsync(buffer, token)) != 0)
            {
                if (bounded.Length + count > options.MaxZipBytes)
                    throw new InvalidDataException(SiteResources.ImportZipLimit);
                await bounded.WriteAsync(buffer.AsMemory(0, count), token);
            }
            bounded.Position = 0;
            var archive = new ZipArchive(bounded, ZipArchiveMode.Read, leaveOpen: true);
            try
            {
                if (archive.Entries.Count > options.MaxEntries)
                    throw new InvalidDataException(SiteResources.ImportEntryLimit);
                var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long expanded = 0;
                foreach (var entry in archive.Entries)
                {
                    string path = entry.FullName;
                    var parts = path.TrimEnd('/').Split('/');
                    if (path.Contains('\\') || parts.Any(x => string.IsNullOrWhiteSpace(x) || x is "." or ".." || x.Contains(':'))
                        || !paths.Add(path.TrimEnd('/')))
                        throw new InvalidDataException(string.Format(SiteResources.ImportInvalidPath, path));
                    if (entry.Length > options.MaxExpandedBytes - expanded)
                        throw new InvalidDataException(SiteResources.ImportExpandedLimit);
                    expanded += entry.Length;
                    if (!path.EndsWith('/')) files.Add(path, entry);
                }
                return new(bounded, archive, files);
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }
        catch
        {
            bounded.Dispose();
            throw;
        }
    }

    public static async Task<string> ReadUtf8Async(ZipArchiveEntry entry, CancellationToken token)
    {
        byte[] data = await ReadEntryAsync(entry, token);
        int offset = data.AsSpan().StartsWith("\uFEFF"u8) ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(data, offset, data.Length - offset);
    }

    public static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, CancellationToken token)
    {
        if (entry.Length > Array.MaxLength) throw new InvalidDataException(string.Format(SiteResources.ImportFileLimit, entry.FullName));
        await using var input = entry.Open();
        var data = new byte[entry.Length];
        if (await input.ReadAtLeastAsync(data, data.Length, throwOnEndOfStream: false, token) != data.Length
            || await input.ReadAsync(new byte[1], token) != 0)
            throw new InvalidDataException(string.Format(SiteResources.ImportFileSizeMismatch, entry.FullName));
        // System.Text.Json accepts UTF-8 without a BOM.
        return entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && data.AsSpan().StartsWith("\uFEFF"u8)
            ? data[3..] : data;
    }

    public void Dispose()
    {
        archive.Dispose();
        stream.Dispose();
    }
}

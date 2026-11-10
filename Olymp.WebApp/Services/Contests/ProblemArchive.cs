using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Olymp.WebApp.Services.Contests;

public sealed class ProblemManifest
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public int TimeLimit { get; set; } = 1;
    public int? SlowTimeLimit { get; set; }
    public int MemoryLimit { get; set; } = 256;
    public int CheckerId { get; set; } = 4;
    public bool IsActive { get; set; } = true;
    public int OpenTestCount { get; set; } = 1;
    public Dictionary<int, int>? Scores { get; set; }
}

public sealed record ImportedTest(int Number, byte[] Input, byte[] Output);
public sealed record ImportedProblem(string Folder, ProblemManifest Manifest, IReadOnlyList<ImportedTest> Tests);

public static partial class ProblemArchive
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [GeneratedRegex("^(input|output)([0-9]+)\\.txt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TestFilename { get; }

    public static async Task<IReadOnlyList<ImportedProblem>> ReadAsync(Stream source, ContestImportOptions options,
        CancellationToken token = default)
    {
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > options.MaxEntries)
            throw new InvalidDataException("Too many files in the archive.");
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName;
            var parts = path.TrimEnd('/').Split('/');
            if (path.Contains('\\') || parts.Any(x => string.IsNullOrWhiteSpace(x) || x is "." or ".." || x.Contains(':')))
                throw new InvalidDataException($"Invalid path: {path}");
            if (path.EndsWith('/'))
            {
                if (parts.Length != 1)
                    throw new InvalidDataException($"Invalid folder: {path}");
                continue;
            }
            if (parts.Length != 2 || !files.TryAdd(path, entry))
                throw new InvalidDataException($"Invalid or duplicate file: {path}");
            if (entry.Length > options.MaxExpandedBytes - expanded)
                throw new InvalidDataException("The expanded archive exceeds the size limit.");
            expanded += entry.Length;
        }

        var result = new List<ImportedProblem>();
        var numbers = new HashSet<int>();
        foreach (var folder in files.Keys.GroupBy(x => x.Split('/')[0], StringComparer.OrdinalIgnoreCase))
        {
            var jsonPath = $"{folder.Key}/problem.json";
            if (!files.TryGetValue(jsonPath, out var json))
                throw new InvalidDataException($"{jsonPath}: file not found.");
            ProblemManifest manifest;
            try
            {
                using var jsonStream = new MemoryStream(await ReadEntryAsync(json, token));
                manifest = await JsonSerializer.DeserializeAsync<ProblemManifest>(jsonStream, JsonOptions, token)
                    ?? throw new JsonException("A JSON object is required.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{jsonPath}: invalid JSON: {ex.Message}", ex);
            }
            if (manifest.Number <= 0 || !numbers.Add(manifest.Number) || string.IsNullOrWhiteSpace(manifest.Name))
                throw new InvalidDataException($"{jsonPath}: a unique positive number and a non-empty name are required.");
            if (manifest.TimeLimit <= 0 || manifest.SlowTimeLimit is <= 0 || manifest.MemoryLimit <= 0 || manifest.CheckerId <= 0)
                throw new InvalidDataException($"{jsonPath}: limits and checkerId must be positive.");

            var inputs = new SortedDictionary<int, ZipArchiveEntry>();
            var outputs = new SortedDictionary<int, ZipArchiveEntry>();
            foreach (var path in folder)
            {
                if (path.Equals(jsonPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                var match = TestFilename.Match(path.Split('/')[1]);
                if (!match.Success || !int.TryParse(match.Groups[2].Value, out var number) || number is < 1 or > 999)
                    throw new InvalidDataException($"{path}: expected inputN.txt or outputN.txt, with N from 1 to 999.");
                var collection = match.Groups[1].Value.Equals("input", StringComparison.OrdinalIgnoreCase) ? inputs : outputs;
                if (!collection.TryAdd(number, files[path]))
                    throw new InvalidDataException($"{path}: duplicate test number {number}.");
            }
            if (inputs.Count == 0 || !inputs.Keys.SequenceEqual(outputs.Keys))
                throw new InvalidDataException($"{folder.Key}: each inputN.txt must have an outputN.txt; at least one test is required.");
            if (manifest.OpenTestCount < 0 || manifest.OpenTestCount > inputs.Count)
                throw new InvalidDataException($"{jsonPath}: openTestCount must be between 0 and {inputs.Count}.");
            if (manifest.Scores is not null && (manifest.Scores.Any(x => !inputs.ContainsKey(x.Key) || x.Value < 0)
                || manifest.Scores.Values.Sum(x => (long)x) > int.MaxValue))
                throw new InvalidDataException($"{jsonPath}: scores contain an unknown test, negative scores or scores that are too large.");
            var tests = new List<ImportedTest>();
            foreach (var (number, input) in inputs)
                tests.Add(new(number, await ReadEntryAsync(input, token), await ReadEntryAsync(outputs[number], token)));
            manifest.Name = manifest.Name.Trim();
            result.Add(new(folder.Key, manifest, tests));
        }
        if (result.Count == 0)
            throw new InvalidDataException("The archive contains no problems.");
        return result;
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, CancellationToken token)
    {
        if (entry.Length > Array.MaxLength)
            throw new InvalidDataException($"{entry.FullName}: the file is too large.");
        await using var stream = entry.Open();
        var data = new byte[entry.Length];
        if (await stream.ReadAtLeastAsync(data, data.Length, throwOnEndOfStream: false, token) != data.Length
            || await stream.ReadAsync(new byte[1], token) != 0)
            throw new InvalidDataException($"{entry.FullName}: the file size does not match the ZIP entry.");
        return data;
    }
}

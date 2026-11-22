using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Contests;

public sealed class ProblemManifest
{
    public int? Id { get; set; }
    public int? Number { get; set; }
    public string? Name { get; set; }
    public int? TimeLimit { get; set; }
    public int? SlowTimeLimit { get; set; }
    public int? MemoryLimit { get; set; }
    public int? CheckerId { get; set; }
    public bool? IsActive { get; set; }
    public int? OpenTestCount { get; set; }
    public Dictionary<int, int>? Scores { get; set; }
    public int[]? InactiveTests { get; set; }
    public Dictionary<int, string>? TestComments { get; set; }
    public int[]? OpenTests { get; set; }
}

public sealed record ImportedTest(int Number, byte[] Input, byte[] Output);
public sealed record ImportedSolution(string Filename, string Source);
public sealed record ImportedProblem(string Folder, ProblemManifest Manifest, IReadOnlyList<ImportedTest> Tests,
    string? Validator, IReadOnlyList<ImportedSolution> Solutions, string? Error = null, string? Statement = null);
public sealed record ImportedContest(int? ContestId, IReadOnlyList<ImportedProblem> Problems);

public static partial class ProblemArchive
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [GeneratedRegex("^(input|output)([0-9]+)\\.txt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TestFilename { get; }

    public static async Task<ImportedContest> ReadAsync(Stream source, ContestImportOptions options,
        CancellationToken token = default)
    {
        using var archive = await ImportArchive.ReadAsync(source, options, token);
        var files = archive.Files;
        var contestFiles = files.Keys.Where(x => x.EndsWith("/contest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (contestFiles.Length > 1 || contestFiles.Any(x => x.Split('/').Length != 2))
            throw new InvalidDataException(SiteResources.ImportSingleContest);
        int? contestId = null;
        string prefix = "";
        if (contestFiles.Length == 1)
        {
            prefix = contestFiles[0][..^"contest.json".Length];
            using var metadata = JsonDocument.Parse(await ImportArchive.ReadEntryAsync(files[contestFiles[0]], token));
            if (metadata.RootElement.TryGetProperty("id", out var id)) contestId = id.GetInt32();
            if (files.Keys.Any(x => !x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(SiteResources.ImportOutsideContest);
        }
        var folders = files.Keys.Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(x => x[prefix.Length..]).Where(x => x.Split('/').Length == 2 && x.EndsWith("/problem.json", StringComparison.OrdinalIgnoreCase))
            .Select(x => prefix + x[..^"/problem.json".Length]).ToArray();
        if (folders.Length == 0) throw new InvalidDataException(SiteResources.ImportNoProblems);
        var result = new List<ImportedProblem>();
        foreach (var folder in folders)
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<ProblemManifest>(await ImportArchive.ReadEntryAsync(files[$"{folder}/problem.json"], token), JsonOptions)
                    ?? throw new InvalidDataException(SiteResources.ImportJsonObject);
                var inputs = new SortedDictionary<int, ZipArchiveEntry>();
                var outputs = new SortedDictionary<int, ZipArchiveEntry>();
                var solutions = new List<ImportedSolution>();
                foreach (var (path, entry) in files.Where(x => x.Key.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    var relative = path[(folder.Length + 1)..];
                    if (relative.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))
                    {
                        var match = TestFilename.Match(relative[6..]);
                        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var number) || number is < 1 or > 999)
                            throw new InvalidDataException(string.Format(SiteResources.ImportTestFilename, path));
                        var collection = match.Groups[1].Value.Equals("input", StringComparison.OrdinalIgnoreCase) ? inputs : outputs;
                        if (!collection.TryAdd(number, entry)) throw new InvalidDataException(string.Format(SiteResources.ImportDuplicateTest, path, number));
                    }
                    else if (relative.StartsWith("solutions/", StringComparison.OrdinalIgnoreCase))
                        solutions.Add(new(relative[10..], await ReadTextAsync(entry, token)));
                }
                if (!inputs.Keys.SequenceEqual(outputs.Keys)) throw new InvalidDataException(SiteResources.ImportMissingPair);
                var tests = new List<ImportedTest>();
                foreach (var (number, input) in inputs)
                    tests.Add(new(number, await ImportArchive.ReadEntryAsync(input, token), await ImportArchive.ReadEntryAsync(outputs[number], token)));
                string? validator = files.TryGetValue($"{folder}/validator.cpp", out var file) ? await ReadTextAsync(file, token) : null;
                string? statement = files.TryGetValue($"{folder}/statement.md", out var markdown) ? await ImportArchive.ReadUtf8Async(markdown, token) : null;
                result.Add(new(folder, manifest, tests, validator, solutions, Statement: statement));
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or System.Text.DecoderFallbackException)
            {
                result.Add(new(folder, new(), [], null, [], ex.Message));
            }
        }
        return new(contestId, result);
    }

    private static async Task<string> ReadTextAsync(ZipArchiveEntry entry, CancellationToken token)
    {
        using var reader = new StreamReader(new MemoryStream(await ImportArchive.ReadEntryAsync(entry, token)), new System.Text.UTF8Encoding(false, true));
        return await reader.ReadToEndAsync(token);
    }
}

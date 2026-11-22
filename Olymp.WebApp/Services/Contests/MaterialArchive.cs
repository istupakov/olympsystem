using System.Text.Json;

using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Contests;

public sealed record ContestMaterials(string Folder, int? ContestId, IReadOnlyDictionary<string, string> Files,
    IReadOnlyDictionary<int, string> Statements, string? Error = null)
{
    public string? Name { get; init; }
    public IReadOnlyList<ParticipantMaterials> Participants { get; init; } = [];
}

public sealed record ParticipantMaterials(int Id, string? Name, bool? IsApproved, bool? IsOutOfCompetition, bool? IsDisqualified);

public static class MaterialArchive
{
    public static async Task<IReadOnlyList<ContestMaterials>> ReadAsync(Stream source, ContestImportOptions options,
        CancellationToken token = default)
    {
        using var archive = await ImportArchive.ReadAsync(source, options, token);
        var files = archive.Files;
        var manifests = files.Keys.Where(p => p.Split('/').Length == 2 && p.EndsWith("/contest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length == 0 || files.Keys.Any(p => !manifests.Any(m => p.StartsWith(m[..^"contest.json".Length], StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException(SiteResources.MaterialArchiveStructure);
        var result = new List<ContestMaterials>();
        var contestIds = new HashSet<int>();
        foreach (string manifest in manifests)
        {
            string prefix = manifest[..^"contest.json".Length];
            int? contestId = null;
            try
            {
                using var metadata = JsonDocument.Parse(await ImportArchive.ReadEntryAsync(files[manifest], token));
                contestId = ReadId(metadata.RootElement);
                if (!contestIds.Add(contestId.Value)) throw new InvalidDataException(SiteResources.MaterialDuplicateId);
                string? name = ReadName(metadata.RootElement);
                var materials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in new[] { "description.html", "tables/final.html", "tables/official.html" })
                    if (files.TryGetValue(prefix + path, out var entry)) materials.Add(path, await ImportArchive.ReadUtf8Async(entry, token));
                var statements = new Dictionary<int, string>();
                foreach (string path in files.Keys.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && p[prefix.Length..].Split('/').Length == 2 && p.EndsWith("/statement.md", StringComparison.OrdinalIgnoreCase)))
                {
                    string problemManifest = path[..^"statement.md".Length] + "problem.json";
                    int problemId = await ReadIdAsync(problemManifest);
                    if (!statements.TryAdd(problemId, await ImportArchive.ReadUtf8Async(files[path], token)))
                        throw new InvalidDataException(SiteResources.MaterialDuplicateId);
                }
                var participants = new List<ParticipantMaterials>();
                if (files.TryGetValue(prefix + "participants.jsonl", out var participantFile))
                {
                    var ids = new HashSet<int>();
                    using var reader = new StringReader(await ImportArchive.ReadUtf8Async(participantFile, token));
                    while (await reader.ReadLineAsync(token) is { } line)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        using var json = JsonDocument.Parse(line);
                        var item = json.RootElement;
                        int id = ReadId(item);
                        if (!ids.Add(id)) throw new InvalidDataException(SiteResources.MaterialDuplicateId);
                        participants.Add(new(id, ReadName(item), ReadFlag(item, "isApproved"),
                            ReadFlag(item, "isOutOfCompetition"), ReadFlag(item, "isDisqualified")));
                    }
                }
                result.Add(new(prefix.TrimEnd('/'), contestId, materials, statements) { Name = name, Participants = participants });
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or System.Text.DecoderFallbackException)
            {
                result.Add(new(prefix.TrimEnd('/'), contestId, new Dictionary<string, string>(), new Dictionary<int, string>(), exception.Message));
            }
        }
        // Neither occurrence of an ambiguous contest ID may be saved.
        var duplicates = result.Where(r => r.ContestId.HasValue).GroupBy(r => r.ContestId).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return [.. result.Select(r => duplicates.Contains(r.ContestId) ? r with { Error = SiteResources.MaterialDuplicateId } : r)];

        async Task<int> ReadIdAsync(string path)
        {
            if (!files.TryGetValue(path, out var entry)) throw new InvalidDataException(SiteResources.MaterialMissingId);
            using var json = JsonDocument.Parse(await ImportArchive.ReadEntryAsync(entry, token));
            return ReadId(json.RootElement);
        }
    }

    private static int ReadId(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int value) || value <= 0)
            throw new InvalidDataException(SiteResources.MaterialMissingId);
        return value;
    }

    private static string? ReadName(JsonElement item)
    {
        if (!item.TryGetProperty("name", out var name)) return null;
        if (name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
            throw new InvalidDataException(string.Format(SiteResources.MaterialInvalidField, "name"));
        return name.GetString()!.Trim();
    }

    private static bool? ReadFlag(JsonElement item, string field)
    {
        if (!item.TryGetProperty(field, out var value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException(string.Format(SiteResources.MaterialInvalidField, field));
        return value.GetBoolean();
    }
}

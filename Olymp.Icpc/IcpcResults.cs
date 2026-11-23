using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Olymp.Icpc;

public sealed record IcpcResultProblem(string Id, string Label, int Ordinal, double TimeLimit = 0, int MemoryLimit = 0, int TestDataCount = 0);
public sealed record IcpcProblemScore(string ProblemId, int NumJudged, bool Solved, TimeSpan? Time);
public sealed record IcpcResultTeam(string Id, string Name, string? IcpcId, int Rank, int Solved,
    TimeSpan TotalTime, TimeSpan LastProblemTime, IReadOnlyList<IcpcProblemScore> Problems);
public sealed record IcpcResults(string Id, string Name, DateTimeOffset Start, DateTimeOffset End,
    IReadOnlyList<IcpcResultProblem> Problems, IReadOnlyList<IcpcResultTeam> Teams);
public sealed record IcpcResultAward(string MedalCitation, string SiteCitation, string Citation);

public static class IcpcResultsWriter
{
    public static IcpcResultAward DefaultAward(IcpcResultTeam team, int? gold, int? silver, int? bronze,
        string siteCitation = "", string citation = "Honorable Mention") => new(
            team.Solved == 0 ? "" : team.Rank <= gold ? "Gold Medal" : team.Rank <= silver ? "Silver Medal"
                : team.Rank <= bronze ? "Bronze Medal" : "",
            siteCitation, team.Solved > 0 ? citation : "");

    public static bool ValidThresholds(int? gold, int? silver, int? bronze)
    {
        int previous = 0;
        foreach (int? value in new[] { gold, silver, bronze })
        {
            if (value is not int rank) continue;
            if (rank <= 0 || rank < previous) return false;
            previous = rank;
        }
        return true;
    }

    public static byte[] Csv(IcpcResults results, IReadOnlyDictionary<string, IcpcResultAward> awards)
    {
        if (results.Teams.Any(t => t.IcpcId is null) || results.Teams.Select(t => t.IcpcId).Distinct().Count() != results.Teams.Count)
            throw new ArgumentException("Unique ICPC team IDs are required.", nameof(results));
        var output = new StringBuilder("teamId,rank,medalCitation,problemsSolved,totalTime,lastProblemTime,siteCitation,citation\r\n");
        foreach (var team in results.Teams)
        {
            var award = awards[team.Id];
            string[] cells = [team.IcpcId!, team.Rank.ToString(CultureInfo.InvariantCulture), award.MedalCitation,
                team.Solved.ToString(CultureInfo.InvariantCulture), Minutes(team.TotalTime), Minutes(team.LastProblemTime), award.SiteCitation, award.Citation];
            output.AppendJoin(',', cells.Select(Escape)).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    public static byte[] Ccs(IcpcResults results, IReadOnlyDictionary<string, IcpcResultAward> awards, DateTimeOffset generated)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Write(string name, object value)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                JsonSerializer.Serialize(stream, value);
            }
            Write("api.json", new { version = "2026-01", version_url = "https://ccs-specs.icpc.io/2026-01/" });
            Write("contest.json", new
            {
                id = results.Id,
                name = results.Name,
                formal_name = results.Name,
                start_time = results.Start,
                duration = Relative(results.End - results.Start),
                scoreboard_type = "pass-fail",
                penalty_time = Relative(TimeSpan.FromMinutes(20))
            });
            Write("teams.json", results.Teams.Select(t => new { id = t.Id, icpc_id = t.IcpcId, label = t.Id, name = t.Name }).ToArray());
            Write("problems.json", results.Problems.Select(p => new
            {
                id = p.Id,
                label = p.Label,
                ordinal = p.Ordinal,
                name = $"Problem {p.Label}",
                time_limit = p.TimeLimit,
                memory_limit = p.MemoryLimit,
                test_data_count = p.TestDataCount
            }).ToArray());
            Write("scoreboard.json", new
            {
                time = generated,
                contest_time = Relative(results.End - results.Start),
                state = new
                {
                    started = results.Start,
                    ended = results.End,
                    frozen = (string?)null,
                    thawed = (string?)null,
                    finalized = (string?)null,
                    end_of_updates = (string?)null
                },
                rows = results.Teams.OrderBy(t => t.Rank)
                    .ThenBy(t => t.Name, StringComparer.Create(CultureInfo.GetCultureInfo("en-US"), ignoreCase: false)).Select(t => new
                    {
                        rank = t.Rank,
                        team_id = t.Id,
                        score = new { num_solved = t.Solved, total_time = Relative(t.TotalTime), time = t.Solved > 0 ? Relative(t.LastProblemTime) : null },
                        problems = t.Problems.Select(p => new Dictionary<string, object?>
                        {
                            ["problem_id"] = p.ProblemId,
                            ["num_judged"] = p.NumJudged,
                            ["num_pending"] = 0,
                            ["solved"] = p.Solved
                        }.Concat(p.Time.HasValue ? new Dictionary<string, object?> { ["time"] = Relative(p.Time.Value) } : [])
                            .ToDictionary(x => x.Key, x => x.Value)).ToArray()
                    }).ToArray()
            });
            var entries = results.Teams.SelectMany(t => new[]
            {
                (Category: "medal", Text: awards[t.Id].MedalCitation, Team: t.Id),
                (Category: "site", Text: awards[t.Id].SiteCitation, Team: t.Id),
                (Category: "citation", Text: awards[t.Id].Citation, Team: t.Id)
            }).Where(a => !string.IsNullOrWhiteSpace(a.Text)).GroupBy(a => (a.Category, a.Text))
                .Select((g, index) => new
                {
                    id = g.Key switch
                    {
                        ("medal", "Gold Medal") => "gold-medal",
                        ("medal", "Silver Medal") => "silver-medal",
                        ("medal", "Bronze Medal") => "bronze-medal",
                        ("citation", "Honorable Mention") => "honorable-mention",
                        _ => $"{g.Key.Category}-{index + 1}"
                    },
                    citation = g.Key.Text,
                    team_ids = g.Select(a => a.Team).ToArray()
                }).ToArray();
            if (entries.Length != 0) Write("awards.json", entries);
        }
        return output.ToArray();
    }

    public static string Relative(TimeSpan time) => string.Create(CultureInfo.InvariantCulture, $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}");
    private static string Minutes(TimeSpan time) => Math.Floor(time.TotalMinutes).ToString(CultureInfo.InvariantCulture);
    private static string Escape(string text) => text.IndexOfAny([',', '"', '\r', '\n']) < 0 ? text : '"' + text.Replace("\"", "\"\"") + '"';
}

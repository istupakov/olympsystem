using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

using Olymp.Icpc;

using Xunit;

namespace Olymp.Tests.Icpc;

public class IcpcResultsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 5, 0, 0, TimeSpan.Zero);

    private static IcpcResults Results() => new("7", "Contest", Start, Start.AddHours(5),
        [new("91", "3", 0, 1.5, 256, 12)],
        [new("11", "Команда", "101", 1, 1, TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(20),
            [new("91", 2, true, TimeSpan.FromMinutes(20))]),
         new("12", "Zero", "102", 2, 0, TimeSpan.Zero, TimeSpan.Zero, [new("91", 3, false, null)])]);

    [Fact]
    public void CsvMatchesEightColumnFormatAndEscapesCustomCitations()
    {
        var results = Results();
        var awards = results.Teams.ToDictionary(t => t.Id, t => IcpcResultsWriter.DefaultAward(t, 1, null, null));
        awards["11"] = awards["11"] with { SiteCitation = "City, \"A\"\r\nSite" };
        string csv = Encoding.UTF8.GetString(IcpcResultsWriter.Csv(results, awards));
        Assert.Equal("teamId,rank,medalCitation,problemsSolved,totalTime,lastProblemTime,siteCitation,citation\r\n"
            + "101,1,Gold Medal,1,40,20,\"City, \"\"A\"\"\r\nSite\",Honorable Mention\r\n"
            + "102,2,,0,0,0,,\r\n", csv);
    }

    [Fact]
    public void CcsContainsConsistentReferencesRelativeTimesAndGroupedAwards()
    {
        var results = Results();
        var awards = results.Teams.ToDictionary(t => t.Id, t => IcpcResultsWriter.DefaultAward(t, 3, null, null));
        using var stream = new MemoryStream(IcpcResultsWriter.Ccs(results, awards, Start.AddDays(1)));
        using var zip = new ZipArchive(stream);
        Assert.Equal(6, zip.Entries.Count);
        JsonElement Read(string name)
        {
            using var entry = zip.GetEntry(name)!.Open();
            using var json = JsonDocument.Parse(entry);
            return json.RootElement.Clone();
        }
        Assert.Equal("2026-01", Read("api.json").GetProperty("version").GetString());
        var contest = Read("contest.json");
        Assert.Equal("pass-fail", contest.GetProperty("scoreboard_type").GetString());
        Assert.Equal("0:20:00.000", contest.GetProperty("penalty_time").GetString());
        Assert.Equal("Problem 3", Read("problems.json")[0].GetProperty("name").GetString());
        var rows = Read("scoreboard.json").GetProperty("rows");
        Assert.Equal("0:40:00.000", rows[0].GetProperty("score").GetProperty("total_time").GetString());
        Assert.Equal(2, rows[0].GetProperty("problems")[0].GetProperty("num_judged").GetInt32());
        Assert.False(rows[1].GetProperty("problems")[0].TryGetProperty("time", out _));
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("score").GetProperty("time").ValueKind);
        Assert.Equal(["gold-medal", "honorable-mention"], Read("awards.json").EnumerateArray().Select(a => a.GetProperty("id").GetString()));
        Assert.All(Read("awards.json").EnumerateArray(), a => Assert.Equal("11", a.GetProperty("team_ids")[0].GetString()));
    }

    [Fact]
    public void CcsWorksWithoutExternalIdsOrAwardsWhileCsvRequiresIds()
    {
        var results = Results();
        results = results with { Teams = [results.Teams[0] with { IcpcId = null }] };
        var awards = new Dictionary<string, IcpcResultAward> { ["11"] = new("", "", "") };
        Assert.Throws<ArgumentException>(() => IcpcResultsWriter.Csv(results, awards));
        using var stream = new MemoryStream(IcpcResultsWriter.Ccs(results, awards, Start));
        using var zip = new ZipArchive(stream);
        Assert.Null(zip.GetEntry("awards.json"));
        results = Results() with { Teams = [Results().Teams[0], Results().Teams[1] with { IcpcId = "101" }] };
        Assert.Throws<ArgumentException>(() => IcpcResultsWriter.Csv(results, awards));
    }

    [Fact]
    public void CcsSortsTiedRowsAlphabeticallyWithoutChangingRanks()
    {
        var results = Results();
        results = results with { Teams = [results.Teams[0] with { Name = "Zulu" }, results.Teams[0] with { Id = "12", Name = "Alpha", IcpcId = "102" }] };
        var awards = results.Teams.ToDictionary(t => t.Id, _ => new IcpcResultAward("", "", ""));
        using var stream = new MemoryStream(IcpcResultsWriter.Ccs(results, awards, Start));
        using var zip = new ZipArchive(stream);
        using var entry = zip.GetEntry("scoreboard.json")!.Open();
        using var json = JsonDocument.Parse(entry);
        var rows = json.RootElement.GetProperty("rows");
        Assert.Equal("12", rows[0].GetProperty("team_id").GetString());
        Assert.Equal("11", rows[1].GetProperty("team_id").GetString());
        Assert.All(rows.EnumerateArray(), r => Assert.Equal(1, r.GetProperty("rank").GetInt32()));
    }

    [Theory]
    [InlineData(1, "Gold Medal")]
    [InlineData(3, "Gold Medal")]
    [InlineData(4, "Silver Medal")]
    [InlineData(6, "Silver Medal")]
    [InlineData(7, "Bronze Medal")]
    [InlineData(11, "Bronze Medal")]
    [InlineData(12, "")]
    public void MedalThresholdsAreInclusiveAndIgnoreZeroSolves(int rank, string expected)
    {
        var team = Results().Teams[0] with { Rank = rank };
        Assert.Equal(expected, IcpcResultsWriter.DefaultAward(team, 3, 6, 11).MedalCitation);
        Assert.Equal(new("", "", ""), IcpcResultsWriter.DefaultAward(team with { Solved = 0 }, 3, 6, 11));
    }

    [Fact]
    public void ThresholdsAndLongDurationsAreCultureIndependent()
    {
        Assert.True(IcpcResultsWriter.ValidThresholds(null, 6, 11));
        Assert.True(IcpcResultsWriter.ValidThresholds(3, 3, 11));
        Assert.False(IcpcResultsWriter.ValidThresholds(6, 3, 11));
        Assert.False(IcpcResultsWriter.ValidThresholds(0, null, null));
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal("27:03:04.005", IcpcResultsWriter.Relative(new TimeSpan(1, 3, 3, 4, 5)));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}

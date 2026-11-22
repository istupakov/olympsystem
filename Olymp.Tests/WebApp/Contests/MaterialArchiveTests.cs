using System.Text;

using Olymp.WebApp.Services.Contests;

using Xunit;

using static Olymp.Tests.Shared.ProblemArchives;

namespace Olymp.Tests.WebApp.Contests;

public class MaterialArchiveTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadsNamesAndPartialParticipantStatusesWithoutDefaultingMissingFields()
    {
        using var stream = new MemoryStream(Zip(
            ("one/contest.json", "{\"id\":1,\"name\":\" New contest \"}"u8.ToArray()),
            ("one/participants.jsonl", "\n{\"id\":12,\"name\":\" New team \",\"isApproved\":false,\"isOutOfCompetition\":true}\n{\"id\":13,\"isDisqualified\":false}\n"u8.ToArray())));
        var row = Assert.Single(await MaterialArchive.ReadAsync(stream, new(), Token));
        Assert.Null(row.Error);
        Assert.Equal("New contest", row.Name);
        Assert.Equal(new ParticipantMaterials(12, "New team", false, true, null), row.Participants[0]);
        Assert.Equal(new ParticipantMaterials(13, null, null, null, false), row.Participants[1]);
    }

    [Theory]
    [InlineData("{\"id\":1,\"name\":null}", "")]
    [InlineData("{\"id\":1,\"name\":\" \"}", "")]
    [InlineData("{\"id\":1,\"name\":42}", "")]
    [InlineData("{\"id\":1}", "{\"id\":2,\"name\":\"\"}")]
    [InlineData("{\"id\":1}", "{\"id\":2,\"isApproved\":null}")]
    [InlineData("{\"id\":1}", "{\"id\":2,\"isOutOfCompetition\":\"false\"}")]
    [InlineData("{\"id\":1}", "{\"id\":2,\"isDisqualified\":1}")]
    [InlineData("{\"id\":1}", "{\"name\":\"Team\"}")]
    [InlineData("{\"id\":1}", "{\"id\":2}\n{\"id\":2}")]
    [InlineData("{\"id\":1}", "[]")]
    public async Task InvalidNamesAndParticipantRecordsOnlyRejectTheirContest(string contest, string participants)
    {
        using var stream = new MemoryStream(Zip(("bad/contest.json", Encoding.UTF8.GetBytes(contest)),
            ("bad/participants.jsonl", Encoding.UTF8.GetBytes(participants)), ("good/contest.json", "{\"id\":3}"u8.ToArray())));
        var rows = await MaterialArchive.ReadAsync(stream, new(), Token);
        Assert.NotNull(rows.Single(r => r.Folder == "bad").Error);
        Assert.Null(rows.Single(r => r.Folder == "good").Error);
    }

    [Fact]
    public async Task EmptyParticipantListAndMissingNameContainNoUpdates()
    {
        using var stream = new MemoryStream(Zip(("one/contest.json", "{\"id\":1}"u8.ToArray()), ("one/participants.jsonl", " \n"u8.ToArray())));
        var row = Assert.Single(await MaterialArchive.ReadAsync(stream, new(), Token));
        Assert.Null(row.Error);
        Assert.Null(row.Name);
        Assert.Empty(row.Participants);
    }

    [Fact]
    public async Task ReadsMultiplePartialContestsAndDistinguishesMissingAndEmptyFiles()
    {
        using var stream = new MemoryStream(Zip(
            ("old/contest.json", "{\"id\":1}"u8.ToArray()),
            ("old/tables/final.html", []),
            ("old/description.html", Encoding.UTF8.GetPreamble().Concat("Описание"u8.ToArray()).ToArray()),
            ("old/problem01/problem.json", "{\"id\":23}"u8.ToArray()),
            ("old/problem01/statement.md", []),
            ("old/statements.pdf", [255]),
            ("old/problem01/tests/input001.txt", [255]),
            ("new/contest.json", "{\"id\":2}"u8.ToArray())));
        var rows = await MaterialArchive.ReadAsync(stream, new(), Token);
        Assert.Equal(2, rows.Count);
        var first = rows.Single(r => r.ContestId == 1);
        Assert.Null(first.Error);
        Assert.Equal("Описание", first.Files["description.html"]);
        Assert.Equal("", first.Files["tables/final.html"]);
        Assert.False(first.Files.ContainsKey("tables/official.html"));
        Assert.Equal("", first.Statements[23]);
        Assert.Empty(rows.Single(r => r.ContestId == 2).Files);
    }

    [Fact]
    public async Task DuplicateContestIdsInvalidateBothRows()
    {
        using var stream = new MemoryStream(Zip(("one/contest.json", "{\"id\":1}"u8.ToArray()), ("two/contest.json", "{\"id\":1}"u8.ToArray())));
        Assert.All(await MaterialArchive.ReadAsync(stream, new(), Token), row => Assert.NotNull(row.Error));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":0}")]
    [InlineData("{\"id\":\"1\"}")]
    [InlineData("[]")]
    public async Task InvalidIdsAreIsolatedFromOtherContests(string metadata)
    {
        using var stream = new MemoryStream(Zip(("bad/contest.json", Encoding.UTF8.GetBytes(metadata)), ("good/contest.json", "{\"id\":2}"u8.ToArray())));
        var rows = await MaterialArchive.ReadAsync(stream, new(), Token);
        Assert.NotNull(rows.Single(r => r.Folder == "bad").Error);
        Assert.Null(rows.Single(r => r.Folder == "good").Error);
    }

    [Fact]
    public async Task ReportsMissingProblemManifestAndInvalidUtf8PerContest()
    {
        foreach (var entry in new[] { ("one/problem01/statement.md", "text"u8.ToArray()), ("one/description.html", [255]) })
        {
            using var stream = new MemoryStream(Zip(("one/contest.json", "{\"id\":1}"u8.ToArray()), entry));
            Assert.NotNull(Assert.Single(await MaterialArchive.ReadAsync(stream, new(), Token)).Error);
        }
    }

    [Theory]
    [InlineData("../ignored")]
    [InlineData("one/../ignored")]
    [InlineData("/one/ignored")]
    [InlineData("one\\ignored")]
    public async Task AppliesSharedPathChecksToIgnoredFiles(string path)
    {
        using var stream = new MemoryStream(Zip(("one/contest.json", "{\"id\":1}"u8.ToArray()), (path, [])));
        await Assert.ThrowsAsync<InvalidDataException>(() => MaterialArchive.ReadAsync(stream, new(), Token));
    }

    [Fact]
    public async Task AppliesSharedSizeAndDuplicateChecks()
    {
        var data = Zip(("one/contest.json", "{\"id\":1}"u8.ToArray()), ("one/ignored", new byte[1024]));
        foreach (var options in new[] { new ContestImportOptions { MaxZipBytes = 10 }, new() { MaxExpandedBytes = 100 }, new() { MaxEntries = 1 } })
        {
            using var stream = new MemoryStream(data);
            await Assert.ThrowsAsync<InvalidDataException>(() => MaterialArchive.ReadAsync(stream, options, Token));
        }
        using var duplicate = new MemoryStream(Zip(("one/contest.json", "{}"u8.ToArray()), ("ONE/CONTEST.JSON", "{}"u8.ToArray())));
        await Assert.ThrowsAsync<InvalidDataException>(() => MaterialArchive.ReadAsync(duplicate, new(), Token));
    }
}

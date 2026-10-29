using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Olymp.Site;
using Olymp.Site.Services.Contests;

using Xunit;

using static Olymp.Tests.Shared.ProblemArchives;

namespace Olymp.Tests.Site.Contests;

public class ProblemArchiveTests
{
    private static readonly IStringLocalizer Localizer = new ResourceManagerStringLocalizerFactory(
        Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }), NullLoggerFactory.Instance)
        .Create(typeof(SharedResource));

    [Fact]
    public async Task ReadsSortedTestsAndMetadataWithoutChangingBytes()
    {
        using var stream = new MemoryStream(ProblemZip(scores: new() { [2] = 100 }));
        var problem = Assert.Single(await ProblemArchive.ReadAsync(stream, new(), Localizer, TestContext.Current.CancellationToken));
        Assert.Equal("Задача", problem.Manifest.Name);
        Assert.Equal(1, problem.Manifest.OpenTestCount);
        Assert.Equal([1, 2], problem.Tests.Select(x => x.Number));
        Assert.Equal(new byte[] { 0, 255, 13, 10 }, problem.Tests[0].Input);
        Assert.Equal(100, problem.Manifest.Scores![2]);
    }

    [Theory]
    [InlineData("../problem.json")]
    [InlineData("A/../problem.json")]
    [InlineData("A\\problem.json")]
    [InlineData("/A/problem.json")]
    [InlineData("A/B/problem.json")]
    public async Task RejectsInvalidPaths(string path)
    {
        using var stream = new MemoryStream(Zip((path, "{}"u8.ToArray())));
        await Assert.ThrowsAsync<InvalidDataException>(() => ProblemArchive.ReadAsync(stream, new(), Localizer, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsMissingPairsDuplicateFilesAndExpansionLimits()
    {
        var json = "{\"number\":1,\"name\":\"A\"}"u8.ToArray();
        foreach (var zip in new[] {
            Zip(("A/problem.json", json), ("A/input1.txt", [1])),
            Zip(("A/problem.json", json), ("A/problem.json", json)),
            Zip(("A/problem.json", json), ("A/input1.txt", [1]), ("A/input01.txt", [2]), ("A/output1.txt", [3])) })
        {
            using var stream = new MemoryStream(zip);
            await Assert.ThrowsAsync<InvalidDataException>(() => ProblemArchive.ReadAsync(stream, new(), Localizer, TestContext.Current.CancellationToken));
        }
        using var oversized = new MemoryStream(ProblemZip());
        await Assert.ThrowsAsync<InvalidDataException>(() => ProblemArchive.ReadAsync(oversized, new() { MaxExpandedBytes = 1 }, Localizer, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AcceptsUtf8BomInProblemJson()
    {
        var json = new byte[] { 0xEF, 0xBB, 0xBF }.Concat("{\"number\":1,\"name\":\"A\"}"u8.ToArray()).ToArray();
        using var stream = new MemoryStream(Zip(("A/problem.json", json), ("A/input1.txt", [1]), ("A/output1.txt", [2])));
        Assert.Single(await ProblemArchive.ReadAsync(stream, new(), Localizer, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public async Task RejectsInvalidExampleCount(int count)
    {
        using var stream = new MemoryStream(ProblemZip(openTestCount: count));
        await Assert.ThrowsAsync<InvalidDataException>(() => ProblemArchive.ReadAsync(stream, new(), Localizer, TestContext.Current.CancellationToken));
    }
}

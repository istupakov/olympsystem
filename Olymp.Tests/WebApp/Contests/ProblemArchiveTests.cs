using Olymp.WebApp.Services.Contests;

using Xunit;

using static Olymp.Tests.Shared.ProblemArchives;

namespace Olymp.Tests.WebApp.Contests;

public class ProblemArchiveTests
{
    [Fact]
    public async Task ReadsSortedTestsAndMetadataWithoutChangingBytes()
    {
        using var stream = new MemoryStream(ProblemZip(scores: new() { [2] = 100 }));
        var package = await ProblemArchive.ReadAsync(stream, new(), TestContext.Current.CancellationToken);
        var problem = Assert.Single(package.Problems);
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
    public async Task RejectsInvalidPaths(string path)
    {
        using var stream = new MemoryStream(Zip((path, "{}"u8.ToArray())));
        await Assert.ThrowsAsync<InvalidDataException>(() => ProblemArchive.ReadAsync(stream, new(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReportsBrokenPairsAndDuplicateTestNumbersPerProblem()
    {
        var json = "{\"number\":1,\"name\":\"A\"}"u8.ToArray();
        foreach (var zip in new[] {
            Zip(("A/problem.json", json), ("A/tests/input1.txt", [1])),
            Zip(("A/problem.json", json), ("A/tests/input1.txt", [1]), ("A/tests/input01.txt", [2]), ("A/tests/output1.txt", [3])) })
        {
            using var stream = new MemoryStream(zip);
            Assert.NotNull(Assert.Single((await ProblemArchive.ReadAsync(stream, new(), TestContext.Current.CancellationToken)).Problems).Error);
        }
    }

    [Fact]
    public async Task RejectsDuplicatePathsAndArchiveLimitsIncludingIgnoredFiles()
    {
        using var duplicate = new MemoryStream(Zip(("A/problem.json", "{}"u8.ToArray()), ("a/PROBLEM.json", "{}"u8.ToArray())));
        await Assert.ThrowsAsync<InvalidDataException>(() => ProblemArchive.ReadAsync(duplicate, new(), TestContext.Current.CancellationToken));
        using var oversized = new MemoryStream(Zip(("A/problem.json", "{}"u8.ToArray()), ("A/statement.html", new byte[1024])));
        await Assert.ThrowsAsync<InvalidDataException>(() => ProblemArchive.ReadAsync(oversized, new() { MaxExpandedBytes = 100 }, TestContext.Current.CancellationToken));
        using var compressed = new MemoryStream(ProblemZip());
        await Assert.ThrowsAsync<InvalidDataException>(() => ProblemArchive.ReadAsync(compressed, new() { MaxZipBytes = 10 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AcceptsExportWrapperUnknownExportFieldsAndEmptyTests()
    {
        using var stream = new MemoryStream(Zip(("contest/contest.json", "{\"id\":123,\"formatVersion\":1}"u8.ToArray()),
            ("contest/problem00/problem.json", "{\"id\":417,\"number\":0,\"name\":\"A\",\"isActive\":false}"u8.ToArray()),
            ("contest/problem00/statement.html", [255]), ("contest/tables/final.html", [255]),
            ("contest/problem00/solutions/main.cpp", "int main(){}"u8.ToArray()),
            ("contest/problem00/validator.cpp", "int main(){}"u8.ToArray())));
        var package = await ProblemArchive.ReadAsync(stream, new(), TestContext.Current.CancellationToken);
        Assert.Equal(123, package.ContestId);
        var problem = Assert.Single(package.Problems);
        Assert.Null(problem.Error);
        Assert.Equal(417, problem.Manifest.Id);
        Assert.Equal(0, problem.Manifest.Number);
        Assert.Empty(problem.Tests);
        Assert.NotNull(problem.Validator);
        Assert.Equal("main.cpp", Assert.Single(problem.Solutions).Filename);
    }

    [Fact]
    public async Task AcceptsUtf8BomAndKeepsOmittedParametersUnset()
    {
        var json = new byte[] { 0xEF, 0xBB, 0xBF }.Concat("{\"id\":417,\"timeLimit\":2}"u8.ToArray()).ToArray();
        using var stream = new MemoryStream(Zip(("A/problem.json", json)));
        var problem = Assert.Single((await ProblemArchive.ReadAsync(stream, new(), TestContext.Current.CancellationToken)).Problems);
        Assert.Null(problem.Error);
        Assert.Null(problem.Manifest.Name);
        Assert.Null(problem.Manifest.SlowTimeLimit);
    }
}

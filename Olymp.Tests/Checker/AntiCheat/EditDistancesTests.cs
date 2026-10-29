using Olymp.Checker.AntiCheat;

using Xunit;

namespace Olymp.Tests.Checker.AntiCheat;

public class EditDistancesTests
{
    [Theory]
    [InlineData("", "", 0)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("abc", "abc", 0)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("a", "b", 1)]
    [InlineData("ab", "ba", 2)]
    [InlineData("привет", "привёт", 1)]
    public void DistanceCountsInsertionsDeletionsAndReplacements(string first, string second, float expected)
    {
        Assert.Equal(expected, EditDistances.LevenshteinDistance(first, second));
        Assert.Equal(expected, EditDistances.LevenshteinDistance(second, first));
    }

    [Theory]
    [InlineData("", "", 0, 0)]
    [InlineData("", "abc", 0, 0)]
    [InlineData("abc", "abc", 1, 1)]
    [InlineData("abc", "xyz", 0, 0)]
    [InlineData("ab", "a", 0.5, 2.0 / 3)]
    [InlineData("abc", "axc", 2.0 / 3, 2.0 / 3)]
    [InlineData("abc", "acb", 1.0 / 3, 2.0 / 3)]
    public void SimilaritiesUseTheirRespectiveNormalizationAndHandleEmptyInput(string first, string second,
        double levenshtein, double subsequence)
    {
        Assert.Equal(levenshtein, EditDistances.LevenshteinSimilarity(first, second), 6);
        Assert.Equal(subsequence, EditDistances.LongestCommonSubsequenceSimilarity(first, second), 6);
    }
}

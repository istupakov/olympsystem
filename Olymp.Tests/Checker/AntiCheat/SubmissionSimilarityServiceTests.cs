using Olymp.Checker.AntiCheat;

using Xunit;

namespace Olymp.Tests.Checker.AntiCheat;

public class SubmissionSimilarityServiceTests
{
    [Theory]
    [InlineData("int main() { return 42; }", "int main() { return 42; }", 1)]
    [InlineData("a = b + 1;", "renamed = other + 999;", 1)]
    [InlineData("имя = значение + 1;", "a=b+2;", 1)]
    [InlineData("a=b+1;", " \na \t= b + 1 ;\r\n", 1)]
    [InlineData("a+b", "a-b", 0)]
    [InlineData("a=b+1;", "a=b-1;", 2.0 / 3)]
    [InlineData("pass", "break", 0)]
    [InlineData("", "", 0)]
    [InlineData(" \r\n", "name_123", 0)]
    [InlineData("pass", "print(1)", 0)]
    public void SimilarityUsesStructuralCharactersAndReturnsFiniteScore(string first, string second, double expected)
    {
        var service = new SimpleSubmissionSimilarityService();

        var similarity = service.CompareSimilarity(first, second);

        Assert.True(float.IsFinite(similarity));
        Assert.InRange(similarity, 0, 1);
        Assert.Equal(expected, similarity, 6);
        Assert.Equal(similarity, service.CompareSimilarity(second, first));
    }
}

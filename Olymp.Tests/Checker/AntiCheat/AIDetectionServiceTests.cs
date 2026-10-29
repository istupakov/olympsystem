using Olymp.Checker.AntiCheat;

using Xunit;

namespace Olymp.Tests.Checker.AntiCheat;

public class AIDetectionServiceTests
{
    [Theory]
    [InlineData("", "C++", 0, 0, 0.5)]
    [InlineData("int main() { return 0; }", "C++", 0, 0, 0.5)]
    [InlineData("// Русский комментарий", "C++", 1, 0, 0.6224593312018546)]
    [InlineData("// English explanation", "Java", 0, 1, 0.5621765008857981)]
    [InlineData("# Русский комментарий\n# English explanation", "Python", 1, 1, 0.679178699175393)]
    [InlineData("# Русский комментарий\n// English explanation", "Python", 1, 0, 0.6224593312018546)]
    [InlineData("# Русский комментарий\n// English explanation", "C#", 0, 1, 0.5621765008857981)]
    [InlineData("// Русский comment", "Kotlin", 1, 0, 0.6224593312018546)]
    public void ScoreAndCommentCountsPreserveExistingHeuristic(string source, string language,
        int cyrillic, int english, double score)
    {
        var result = new SimpleAIDetectionService().DetectAI(source, language);

        Assert.Equal(cyrillic, result.CyrillicComments);
        Assert.Equal(english, result.EnglishComments);
        Assert.Equal(score, result.Score, 12);
    }

    [Theory]
    [InlineData("// int value = 0;")]
    [InlineData("// for (int i = 0; i < n; ++i)")]
    [InlineData("// 12345")]
    [InlineData("// Этот файл содержит функцию \"main\". Здесь начинается и заканчивается выполнение программы.")]
    public void CodeNumericAndGeneratedTemplateCommentsDoNotContribute(string source)
    {
        var result = new SimpleAIDetectionService().DetectAI(source, "C++");

        Assert.Equal(0.5, result.Score);
        Assert.Equal(0, result.CyrillicComments);
        Assert.Equal(0, result.EnglishComments);
        Assert.Empty(result.SuspiciousFragments);
    }

    [Fact]
    public void SuspiciousFragmentsContainOriginalCommentsWithCyrillicGroupFirst()
    {
        var result = new SimpleAIDetectionService().DetectAI(
            "// English explanation\n// Русский комментарий\n// 1234\n", "C++");

        Assert.Equal([" Русский комментарий", " English explanation"], result.SuspiciousFragments);
    }

    [Fact]
    public void RepeatedCallsDoNotChangePreviouslyReturnedResult()
    {
        var service = new SimpleAIDetectionService();
        var first = service.DetectAI("# Русский комментарий", "Python");

        var second = service.DetectAI("", "Python");

        Assert.Equal([" Русский комментарий"], first.SuspiciousFragments);
        Assert.Empty(second.SuspiciousFragments);
        Assert.True(first.Score > second.Score);
    }
}

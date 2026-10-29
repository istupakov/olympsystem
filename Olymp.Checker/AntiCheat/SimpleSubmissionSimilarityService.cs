using System.Text.RegularExpressions;

namespace Olymp.Checker.AntiCheat;

public sealed partial class SimpleSubmissionSimilarityService : ISubmissionSimilarityService
{
    [GeneratedRegex(@"[\w\s]")]
    private static partial Regex FilterRegex { get; }

    public float CompareSimilarity(string source1, string source2)
    {
        source1 = FilterRegex.Replace(source1, string.Empty);
        source2 = FilterRegex.Replace(source2, string.Empty);
        return EditDistances.LevenshteinSimilarity(source1, source2);
    }
}

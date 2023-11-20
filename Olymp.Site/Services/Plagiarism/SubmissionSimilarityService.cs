using System.Text.RegularExpressions;

using Olymp.Domain.Models;

namespace Olymp.Site.Services.Plagiarism;

public interface ISubmissionSimilarityService
{
    float Similarity(Submission submission1, Submission submission2);
}

public partial class SimpleSubmissionSimilarityService : ISubmissionSimilarityService
{
    public float Similarity(Submission submission1, Submission submission2)
    {
        var source1 = FilterRegex().Replace(submission1.Text, string.Empty);
        var source2 = FilterRegex().Replace(submission2.Text, string.Empty);
        return EditDistances.LevenshteinSimilarity(source1, source2);
    }

    [GeneratedRegex("[\\w\\s]")]
    private static partial Regex FilterRegex();
}

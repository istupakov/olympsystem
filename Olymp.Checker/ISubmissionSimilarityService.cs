namespace Olymp.Checker;

public interface ISubmissionSimilarityService
{
    float CompareSimilarity(string source1, string source2);
}

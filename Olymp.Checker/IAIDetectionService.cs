namespace Olymp.Checker;

public interface IAIDetectionService
{
    AIDetectionResult DetectAI(string source, string language);
}

public sealed record AIDetectionResult(double Score, int CyrillicComments, int EnglishComments,
    IReadOnlyList<string> SuspiciousFragments);

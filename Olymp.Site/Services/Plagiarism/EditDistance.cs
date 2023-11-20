namespace Olymp.Site.Services.Plagiarism;

public static class EditDistances
{
    public static float LevenshteinDistance(string source1, string source2, float insertCost = 1, float replaceCost = 1)
    {
        var row = new float[source2.Length + 1];
        for (int j = 0; j <= source2.Length; ++j)
            row[j] = j;

        for (int i = 0; i < source1.Length; ++i)
        {
            row[0] = i + 1;
            float prevAbove = i, prevDiag;
            for (int j = 0; j < source2.Length; ++j)
            {
                (prevDiag, prevAbove) = (prevAbove, row[j + 1]);
                row[j + 1] = Math.Min(Math.Min(prevAbove, row[j]) + insertCost,
                    prevDiag + (source1[i] != source2[j] ? replaceCost : 0));
            }
        }

        return row[source2.Length];
    }

    public static float LevenshteinSimilarity(string source1, string source2)
    {
        return 1 - LevenshteinDistance(source1, source2) / Math.Max(source1.Length, source2.Length);
    }

    public static float LongestCommonSubsequenceSimilarity(string source1, string source2)
    {
        return 1 - LevenshteinDistance(source1, source2, replaceCost: 2) / (source1.Length + source2.Length);
    }
}

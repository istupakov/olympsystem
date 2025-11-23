using System.Text.RegularExpressions;

using Olymp.Domain.Models;

namespace Olymp.Site.Services.AntiCheat;

public record AIDetectionResult(double Probability, int CyrillicComments, int EnglishComments, IEnumerable<string> SuspiciousFragments);

public interface IAIDetectionServiceService
{
    AIDetectionResult DetectAI(Submission submission);
}

public partial class SimpleAIDetectionServiceService : IAIDetectionServiceService
{
    [GeneratedRegex(@"#(.*)$", RegexOptions.Multiline)]
    private static partial Regex PythonComments { get; }

    [GeneratedRegex(@"//(.*)$", RegexOptions.Multiline)]
    private static partial Regex CppStyleComments { get; }

    [GeneratedRegex(@"^(.+[;:\{)>\]]\s*|\s*[\{\}]\s*)$")]
    private static partial Regex CodeRegex { get; }

    [GeneratedRegex(@"\p{IsCyrillic}")]
    private static partial Regex CyrillicRegex { get; }

    [GeneratedRegex(@"[A-Za-z]")]
    private static partial Regex EnglishRegex { get; }

    private static double Sigmoid(double x) => 1.0 / (1 + Math.Exp(-x));

    public AIDetectionResult DetectAI(Submission submission)
    {
        var comments = (submission.Compilator.Language == "Python" ? PythonComments : CppStyleComments)
            .Matches(submission.Text).Select(x => x.Groups[1].Value);

        comments = [.. comments.Where(x => !x.Contains("Этот файл содержит функцию \"main\". Здесь начинается и заканчивается выполнение программы."))];

        var cyrillicComments = comments.Where(x => CyrillicRegex.IsMatch(x)).ToList();
        var englishComments = comments.Where(x => !CodeRegex.IsMatch(x) && EnglishRegex.IsMatch(x)).Except(cyrillicComments).ToList();

        return new AIDetectionResult(Sigmoid(cyrillicComments.Count / 2.0 + englishComments.Count / 4.0), cyrillicComments.Count, englishComments.Count, cyrillicComments.Concat(englishComments));
    }
}

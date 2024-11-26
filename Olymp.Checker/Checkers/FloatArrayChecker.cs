using System.Globalization;

namespace Olymp.Checker.Checkers;

public class FloatArrayChecker : ISimpleChecker
{
    public int Id => 2;

    private static CheckerResultStatus CheckInternal(string juryOutput, string userOutput)
    {
        var juryStringCount = juryOutput.Split(['\n'], StringSplitOptions.RemoveEmptyEntries).Length;
        var userStringCount = userOutput.Split(['\n'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (juryStringCount != userStringCount) return CheckerResultStatus.PresentationError;

        var juryAnswer = juryOutput.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => Convert.ToDouble(t, CultureInfo.InvariantCulture)).ToList();
        var userAnswer = userOutput.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => Convert.ToDouble(t, CultureInfo.InvariantCulture)).ToList();

        if (juryAnswer.Count != userAnswer.Count)
            return CheckerResultStatus.PresentationError;
        return juryAnswer.Zip(userAnswer, (a, b) => Math.Abs(a - b)).All(t => t <= 1e-3) ? CheckerResultStatus.Accepted : CheckerResultStatus.WrongAnswer;
    }

    public CheckerResultStatus Check(string juryInput, string juryOutput, string userOutput)
    {
        try
        {
            return CheckInternal(juryOutput, userOutput);
        }
        catch
        {
            return CheckerResultStatus.PresentationError;
        }
    }
}

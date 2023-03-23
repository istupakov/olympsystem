namespace Olymp.Checker.Checkers;

public class DefaultStrictChecker : ISimpleChecker
{
    public int Id => 4;

    public CheckerResultStatus Check(string juryInput, string juryOutput, string userOutput)
    {
        var juryLines = MemoryExtensions.EnumerateLines(juryOutput);
        var userLines = MemoryExtensions.EnumerateLines(userOutput);

        while (true)
        {
            var hasJuryLine = juryLines.MoveNext();
            var hasUserLine = userLines.MoveNext();
            if (hasJuryLine ^ hasUserLine)
            {
                if (hasJuryLine && juryLines.Current.IsEmpty && !juryLines.MoveNext())
                    return CheckerResultStatus.Accepted;
                return CheckerResultStatus.PresentationError;
            }
            if (!hasJuryLine && !hasUserLine)
                return CheckerResultStatus.Accepted;
            if (!juryLines.Current.Equals(userLines.Current, StringComparison.Ordinal))
                return CheckerResultStatus.WrongAnswer;
        }
    }
}

namespace Olymp.Checker.Tests.CSharp;

public class TimeLimitTest : ICheckerTest
{
    public string Source => """
            long sum = 0;
            for(long i=0; i<10_000_000_000; ++i)
                sum += i;
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.TimeLimit;
}

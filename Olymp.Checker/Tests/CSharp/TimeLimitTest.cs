namespace Olymp.Checker.Tests.CSharp;

public class TimeLimitTest : ICheckerTest
{
    public string Source => """
            while (true) { }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.TimeLimit;
}

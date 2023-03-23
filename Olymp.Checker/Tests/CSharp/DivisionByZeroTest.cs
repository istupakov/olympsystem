namespace Olymp.Checker.Tests.CSharp;

public class DivisionByZeroTest : ICheckerTest
{
    public string Source => """
            int x = 0;
            int y = 1/x;
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.RuntimeError;
}

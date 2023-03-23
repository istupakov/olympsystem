namespace Olymp.Checker.Tests.Python;

public class DivisionByZeroTest : ICheckerTest
{
    public string Source => """
            x = 0
            y = 1/x
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.RuntimeError;
}

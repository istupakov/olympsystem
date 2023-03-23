namespace Olymp.Checker.Tests.CSharp;

public class NonZeroExitCodeTest : ICheckerTest
{
    public string Source => """
            return -1;
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.RuntimeError;
}

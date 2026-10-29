namespace Olymp.Checker.Tests.Pascal;

public class NonZeroExitCodeTest : ICheckerTest
{
    public string Source => """
            begin
              halt(1)
            end.
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.RuntimeError;
}

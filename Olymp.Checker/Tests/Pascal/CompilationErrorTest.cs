namespace Olymp.Checker.Tests.Pascal;

public class CompilationErrorTest : ICheckerTest
{
    public string Source => """
            begin
              writeln (42
            end.
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.CompilationError;
}

namespace Olymp.Checker.Tests.Python;

public class CompilationErrorTest : ICheckerTest
{
    public string Source => """
            print(42
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.CompilationError;
}

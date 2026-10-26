namespace Olymp.Checker.Tests.Kotlin;

public class CompilationErrorTest : ICheckerTest
{
    public string Source => """
            fun main() {
                println(42
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.CompilationError;
}

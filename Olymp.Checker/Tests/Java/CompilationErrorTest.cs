namespace Olymp.Checker.Tests.Java;

public class CompilationErrorTest : ICheckerTest
{
    public string Source => """
            public class Main {
              public static void main(String[] args) {
                System.out.println(42
              }
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.CompilationError;
}

namespace Olymp.Checker.Tests.Java;

public class DivisionByZeroTest : ICheckerTest
{
    public string Source => """
            public class Main {
              public static void main(String[] args) {
                int x = 0;
                int y = 1/x;
              }
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.RuntimeError;
}

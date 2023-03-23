namespace Olymp.Checker.Tests.Java;

public class AcceptedConstTest : ICheckerTest
{
    public string Source => """
            public class Main {
              public static void main(String[] args) {
                System.out.println(42);
              }
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Output => $"42\r\n";
}

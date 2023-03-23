namespace Olymp.Checker.Tests.Java;

public class WrongAnswerConstTest : ICheckerTest
{
    public string Source => """
            public class Main {
              public static void main(String[] args) {
                System.out.println(41);
              }
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.WrongAnswer;
    public string Output => $"42\r\n";
}

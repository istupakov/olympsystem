namespace Olymp.Checker.Tests.Kotlin;

public class WrongAnswerConstTest : ICheckerTest
{
    public string Source => """
            fun main() {
                println(41)
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.WrongAnswer;
    public string Output => "42\r\n";
}

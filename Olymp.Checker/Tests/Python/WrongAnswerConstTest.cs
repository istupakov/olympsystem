namespace Olymp.Checker.Tests.Python;

public class WrongAnswerConstTest : ICheckerTest
{
    public string Source => """
            print(41)
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.WrongAnswer;
    public string Output => $"42\r\n";
}

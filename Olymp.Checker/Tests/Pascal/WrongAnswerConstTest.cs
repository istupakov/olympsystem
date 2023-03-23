namespace Olymp.Checker.Tests.Pascal;

public class WrongAnswerConstTest : ICheckerTest
{
    public string Source => """
            begin
              writeln (41)
            end.
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.WrongAnswer;
    public string Output => $"42\r\n";
}

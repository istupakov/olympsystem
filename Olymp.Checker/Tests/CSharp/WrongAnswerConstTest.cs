namespace Olymp.Checker.Tests.CSharp;

public class WrongAnswerConstTest : ICheckerTest
{
    public string Source => """
            System.Console.WriteLine(41);
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.WrongAnswer;
    public string Output => $"42\r\n";
}

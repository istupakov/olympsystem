namespace Olymp.Checker.Tests.CSharp;

public class WrongAnswerUnicodeTest : ICheckerTest
{
    public string Source => """
            System.Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            System.Console.WriteLine("Ю");
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.WrongAnswer;
    public string Output => "Я\r\n";
}

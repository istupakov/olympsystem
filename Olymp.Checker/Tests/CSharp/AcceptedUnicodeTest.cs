namespace Olymp.Checker.Tests.CSharp;

public class AcceptedUnicodeTest : ICheckerTest
{
    public string Source => """
            System.Console.InputEncoding = new System.Text.UTF8Encoding(false);
            System.Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            System.Console.WriteLine("Привет, " + System.Console.ReadLine());
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "  мир  ";
    public string Output => "Привет,   мир  \r\n";
}

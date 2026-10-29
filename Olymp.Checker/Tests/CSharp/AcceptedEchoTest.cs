namespace Olymp.Checker.Tests.CSharp;

public class AcceptedEchoTest : ICheckerTest
{
    public string Source => """
            while (System.Console.ReadLine() is string line)
                System.Console.WriteLine(line);
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "\r\n1 2 3\r\n  x  \r\nxxxxxxxxxxxxx\r\n\r\n  last line  ";
    public string Output => Input + "\r\n";
}

namespace Olymp.Checker.Tests.CSharp;

public class AcceptedInputWithoutNewLineTest : ICheckerTest
{
    public string Source => """
            while (System.Console.ReadLine() is string line)
                System.Console.WriteLine(line);
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "first line\r\n  last line  ";
    public string Output => Input + "\r\n";
}

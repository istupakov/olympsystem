namespace Olymp.Checker.Tests.CSharp;

public class AcceptedConstTest : ICheckerTest
{
    public string Source => """
            System.Console.WriteLine(42);
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Output => $"42\r\n";
}

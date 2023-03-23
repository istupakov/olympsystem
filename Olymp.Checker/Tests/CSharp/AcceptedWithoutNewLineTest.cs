namespace Olymp.Checker.Tests.CSharp;

public class AcceptedWithoutNewLineTest : ICheckerTest
{
    public string Source => """
            System.Console.Write(42);
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Output => $"42\r\n";
}

namespace Olymp.Checker.Tests.CSharp;

public class PresentationErrorTest : ICheckerTest
{
    public string Source => """
            System.Console.WriteLine(42);
            System.Console.WriteLine();
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.PresentationError;
    public string Output => $"42\r\n";
}

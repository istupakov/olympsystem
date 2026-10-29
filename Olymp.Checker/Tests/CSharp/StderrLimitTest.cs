namespace Olymp.Checker.Tests.CSharp;

public class StderrLimitTest : ICheckerTest
{
    public string Source => """
            System.Console.Error.Write(new string('x', 4097));
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.PresentationError;
}

namespace Olymp.Checker.Tests.CSharp;

public class StdoutLimitTest : ICheckerTest
{
    public string Source => """
            for(int i=0; i<1025; ++i)
                System.Console.Write(new string('x', 1024));
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.PresentationError;
}

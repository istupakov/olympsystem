namespace Olymp.Checker.Tests.CSharp;

public class StdoutLimitTest : ICheckerTest
{
    public string Source => """
            for(int i=0; i<1024; ++i)
                System.Console.WriteLine(new string('x', 1022));
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.PresentationError;
}

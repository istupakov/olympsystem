namespace Olymp.Checker.Tests.CSharp;

public class StderrLimitTest : ICheckerTest
{
    public string Source => """
            for(int i=0; i<4; ++i)
                System.Console.Error.WriteLine(new string('x', 1022));
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.PresentationError;
}

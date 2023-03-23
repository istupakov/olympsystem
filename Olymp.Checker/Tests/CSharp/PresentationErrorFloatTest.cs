using Olymp.Checker.Checkers;

namespace Olymp.Checker.Tests.CSharp;

public class PresentationErrorFloatTest : ICheckerTest
{
    public string Source => """
            System.Console.WriteLine("x");
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.PresentationError;
    public string Output => $"42\r\n";
    public ISimpleChecker Checker => new FloatArrayChecker();
}

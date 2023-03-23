using Olymp.Checker.Checkers;

namespace Olymp.Checker.Tests.CSharp;

public class WrongAnswerFloatTest : ICheckerTest
{
    public string Source => """
            System.Console.WriteLine(42.0011);
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.WrongAnswer;
    public string Output => $"42\r\n";
    public ISimpleChecker Checker => new FloatArrayChecker();
}

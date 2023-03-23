using Olymp.Checker.Checkers;

namespace Olymp.Checker.Tests.CSharp;

public class AcceptedFloatTest : ICheckerTest
{
    public string Source => """
            System.Console.WriteLine(42.0009);
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Output => $"42\r\n";
    public ISimpleChecker Checker => new FloatArrayChecker();
}

using Olymp.Checker.Checkers;

namespace Olymp.Checker.Tests.Python;

public class AcceptedFloatTest : ICheckerTest
{
    public string Source => """
            a = float(input())
            b = float(input())
            print(a + b)
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "1.5\r\n2.75";
    public string Output => "4.25\r\n";
    public ISimpleChecker Checker => new FloatArrayChecker();
}

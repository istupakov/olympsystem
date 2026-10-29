using Olymp.Checker.Checkers;

namespace Olymp.Checker.Tests.Pascal;

public class AcceptedFloatTest : ICheckerTest
{
    public string Source => """
            var a, b: double;
            begin
              readln(a);
              readln(b);
              writeln(a + b)
            end.
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "1.5\r\n2.75";
    public string Output => "4.25\r\n";
    public ISimpleChecker Checker => new FloatArrayChecker();
}

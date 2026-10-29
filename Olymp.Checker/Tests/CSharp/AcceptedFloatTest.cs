using Olymp.Checker.Checkers;

namespace Olymp.Checker.Tests.CSharp;

public class AcceptedFloatTest : ICheckerTest
{
    public string Source => """
            double a = double.Parse(System.Console.ReadLine());
            double b = double.Parse(System.Console.ReadLine());
            System.Console.WriteLine(a + b);
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "1.5\r\n2.75";
    public string Output => "4.25\r\n";
    public ISimpleChecker Checker => new FloatArrayChecker();
}

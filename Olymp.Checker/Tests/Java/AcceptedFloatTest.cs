using Olymp.Checker.Checkers;

namespace Olymp.Checker.Tests.Java;

public class AcceptedFloatTest : ICheckerTest
{
    public string Source => """
            public class Main {
              public static void main(String[] args) {
                java.util.Scanner input = new java.util.Scanner(System.in);
                System.out.println(input.nextDouble() + input.nextDouble());
              }
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "1.5\r\n2.75";
    public string Output => "4.25\r\n";
    public ISimpleChecker Checker => new FloatArrayChecker();
}

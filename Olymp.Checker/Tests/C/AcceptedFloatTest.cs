using Olymp.Checker.Checkers;

namespace Olymp.Checker.Tests.C;

public class AcceptedFloatTest : ICheckerTest
{
    public string Source => """
            #include <stdio.h>
            int main() {
                double a, b;
                if (scanf("%lf%lf", &a, &b) != 2)
                    return 1;
                printf("%.2f\n", a + b);
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "1.5\r\n2.75";
    public string Output => "4.25\r\n";
    public ISimpleChecker Checker => new FloatArrayChecker();
}

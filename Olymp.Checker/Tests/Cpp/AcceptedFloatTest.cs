using Olymp.Checker.Checkers;

namespace Olymp.Checker.Tests.Cpp;

public class AcceptedFloatTest : ICheckerTest
{
    public string Source => """
            #include <iostream>
            int main() {
                double a, b;
                std::cin >> a >> b;
                std::cout << a + b << '\n';
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "1.5\r\n2.75";
    public string Output => "4.25\r\n";
    public ISimpleChecker Checker => new FloatArrayChecker();
}

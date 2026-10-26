namespace Olymp.Checker.Tests.Cpp;

public class AcceptedInputTest : ICheckerTest
{
    public string Source => """
            #include <iostream>
            int main() {
                int a, b;
                if (!(std::cin >> a >> b))
                    return 1;
                std::cout << a + b << std::endl;
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "17\r\n25\r\n";
    public string Output => "42\r\n";
}

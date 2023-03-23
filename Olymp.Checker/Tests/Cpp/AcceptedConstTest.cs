namespace Olymp.Checker.Tests.Cpp;

public class AcceptedConstTest : ICheckerTest
{
    public string Source => """
            #include <iostream>
            int main() {
                std::cout << 42 << std::endl;
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Output => $"42\r\n";
}

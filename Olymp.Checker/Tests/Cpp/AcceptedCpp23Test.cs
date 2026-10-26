namespace Olymp.Checker.Tests.Cpp;

public class AcceptedCpp23Test : ICheckerTest
{
    public IReadOnlyList<string> CompilatorNames => ["GCC C++23"];

    public string Source => """
            #include <expected>
            #include <iostream>
            int main() {
                std::expected<int, const char*> result(42);
                std::cout << result.value() << '\n';
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Output => "42\r\n";
}

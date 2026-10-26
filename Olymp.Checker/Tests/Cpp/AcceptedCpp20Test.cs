namespace Olymp.Checker.Tests.Cpp;

public class AcceptedCpp20Test : ICheckerTest
{
    public IReadOnlyList<string> CompilatorNames => ["GCC C++20", "GCC C++23", "MSVC C++20"];

    public string Source => """
            #include <iostream>
            #include <span>
            int main() {
                int data[] = {17, 25};
                std::span<const int> values(data);
                std::cout << values[0] + values[1] << '\n';
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Output => "42\r\n";
}

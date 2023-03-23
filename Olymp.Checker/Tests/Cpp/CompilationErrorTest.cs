namespace Olymp.Checker.Tests.Cpp;

public class CompilationErrorTest : ICheckerTest
{
    public string Source => """
            #include <iostream>
            int main() {
                std::cout << 42
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.CompilationError;
}

namespace Olymp.Checker.Tests.Cpp;

public class WrongAnswerConstTest : ICheckerTest
{
    public string Source => """
            #include <iostream>
            int main() {
                std::cout << 41 << std::endl;
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.WrongAnswer;
    public string Output => $"42\r\n";
}

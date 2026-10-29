namespace Olymp.Checker.Tests.Cpp;

public class AcceptedUnicodeTest : ICheckerTest
{
    public string Source => """
            #include <iostream>
            #include <string>
            int main() {
                // Кириллица в исходнике и строковом литерале.
                std::string name;
                std::getline(std::cin, name);
                std::cout << "Привет, " << name << '\n';
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "  мир  ";
    public string Output => "Привет,   мир  \r\n";
}

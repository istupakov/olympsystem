namespace Olymp.Checker.Tests.C;

public class AcceptedUnicodeTest : ICheckerTest
{
    public string Source => """
            #include <stdio.h>
            int main() {
                // Кириллица в исходнике и строковом литерале.
                char name[128];
                if (fgets(name, sizeof(name), stdin) == NULL)
                    return 1;
                printf("Привет, %s\n", name);
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "  мир  ";
    public string Output => "Привет,   мир  \r\n";
}

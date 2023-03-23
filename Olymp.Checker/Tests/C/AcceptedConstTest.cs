namespace Olymp.Checker.Tests.C;

public class AcceptedConstTest : ICheckerTest
{
    public string Source => """
            #include <stdio.h>
            int main() {
                printf("%d", 42);
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Output => $"42\r\n";
}

namespace Olymp.Checker.Tests.C;

public class WrongAnswerConstTest : ICheckerTest
{
    public string Source => """
            #include <stdio.h>
            int main() {
                printf("%d", 41);
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.WrongAnswer;
    public string Output => $"42\r\n";
}

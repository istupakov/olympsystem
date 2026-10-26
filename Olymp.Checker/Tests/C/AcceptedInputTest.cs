namespace Olymp.Checker.Tests.C;

public class AcceptedInputTest : ICheckerTest
{
    public string Source => """
            #include <stdio.h>
            int main() {
                int a, b;
                if (scanf("%d%d", &a, &b) != 2)
                    return 1;
                printf("%d\n", a + b);
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "17\r\n25\r\n";
    public string Output => "42\r\n";
}

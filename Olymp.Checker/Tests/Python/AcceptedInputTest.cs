namespace Olymp.Checker.Tests.Python;

public class AcceptedInputTest : ICheckerTest
{
    public string Source => """
            a = int(input())
            b = int(input())
            print(a + b)
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "17\r\n25\r\n";
    public string Output => "42\r\n";
}

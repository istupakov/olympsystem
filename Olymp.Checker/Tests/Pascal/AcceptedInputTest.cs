namespace Olymp.Checker.Tests.Pascal;

public class AcceptedInputTest : ICheckerTest
{
    public string Source => """
            var a, b: longint;
            begin
              readln(a);
              readln(b);
              writeln(a + b)
            end.
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "17\r\n25\r\n";
    public string Output => "42\r\n";
}

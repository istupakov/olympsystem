namespace Olymp.Checker.Tests.Pascal;

public class AcceptedConstTest : ICheckerTest
{
    public string Source => """
            begin
              writeln (42)
            end.
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Output => $"42\r\n";
}

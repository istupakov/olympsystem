namespace Olymp.Checker.Tests.Python;

public class AcceptedConstTest : ICheckerTest
{
    public string Source => """
            print(42)
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Output => $"42\r\n";
}

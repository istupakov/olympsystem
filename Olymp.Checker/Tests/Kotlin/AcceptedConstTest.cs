namespace Olymp.Checker.Tests.Kotlin;

public class AcceptedConstTest : ICheckerTest
{
    public string Source => """
            fun main() {
                println(42)
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Output => "42\r\n";
}

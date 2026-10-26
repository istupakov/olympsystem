namespace Olymp.Checker.Tests.Kotlin;

public class DivisionByZeroTest : ICheckerTest
{
    public string Source => """
            fun main() {
                val x = readln().toInt()
                println(1 / x)
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.RuntimeError;
    public string Input => "0\r\n";
}

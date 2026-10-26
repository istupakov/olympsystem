namespace Olymp.Checker.Tests.Kotlin;

public class AcceptedInputTest : ICheckerTest
{
    public string Source => """
            fun main() {
                val input = System.`in`.bufferedReader()
                val a = input.readLine().toInt()
                val b = input.readLine().toInt()
                println(a + b)
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "17\r\n25\r\n";
    public string Output => "42\r\n";
}

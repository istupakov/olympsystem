using Olymp.Checker.Checkers;

namespace Olymp.Checker.Tests.Kotlin;

public class AcceptedFloatTest : ICheckerTest
{
    public string Source => """
            fun main() {
                val input = System.`in`.bufferedReader()
                val a = input.readLine().toDouble()
                val b = input.readLine().toDouble()
                println(a + b)
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "1.5\r\n2.75";
    public string Output => "4.25\r\n";
    public ISimpleChecker Checker => new FloatArrayChecker();
}

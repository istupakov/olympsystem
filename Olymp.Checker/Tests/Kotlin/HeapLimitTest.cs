namespace Olymp.Checker.Tests.Kotlin;

public class HeapLimitTest : ICheckerTest
{
    public string Source => """
            fun main() {
                val block = ByteArray(129*1024*1024)
                println(block.size)
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.RuntimeError;
}

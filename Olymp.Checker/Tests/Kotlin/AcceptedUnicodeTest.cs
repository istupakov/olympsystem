namespace Olymp.Checker.Tests.Kotlin;

public class AcceptedUnicodeTest : ICheckerTest
{
    public string Source => """
            fun main() {
                // Кириллица в исходнике и строковом литерале.
                val input = System.`in`.bufferedReader(Charsets.UTF_8)
                val name = input.readLine()
                System.out.write("Привет, $name\n".toByteArray(Charsets.UTF_8))
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "  мир  ";
    public string Output => "Привет,   мир  \r\n";
}

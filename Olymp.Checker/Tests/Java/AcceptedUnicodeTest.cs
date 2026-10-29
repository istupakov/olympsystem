namespace Olymp.Checker.Tests.Java;

public class AcceptedUnicodeTest : ICheckerTest
{
    public string Source => """
            public class Main {
              public static void main(String[] args) throws Exception {
                // Кириллица в исходнике и строковом литерале.
                java.io.BufferedReader input = new java.io.BufferedReader(
                    new java.io.InputStreamReader(System.in, java.nio.charset.StandardCharsets.UTF_8));
                String name = input.readLine();
                System.out.write(("Привет, " + name + "\n").getBytes(java.nio.charset.StandardCharsets.UTF_8));
              }
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "  мир  ";
    public string Output => "Привет,   мир  \r\n";
}

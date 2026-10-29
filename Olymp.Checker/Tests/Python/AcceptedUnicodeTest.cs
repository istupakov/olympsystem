namespace Olymp.Checker.Tests.Python;

public class AcceptedUnicodeTest : ICheckerTest
{
    public string Source => """
            # Кириллица в исходнике и строковом литерале.
            name = input()
            print("Привет, " + name)
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "  мир  ";
    public string Output => "Привет,   мир  \r\n";
}

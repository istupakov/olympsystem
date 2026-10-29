namespace Olymp.Checker.Tests.Pascal;

public class AcceptedUnicodeTest : ICheckerTest
{
    public string Source => """
            var name: UnicodeString;
            begin
              { Кириллица в исходнике и строковом литерале. }
              SetTextCodePage(Input, 65001);
              SetTextCodePage(Output, 65001);
              readln(name);
              writeln(UnicodeString('Привет, ') + name)
            end.
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "  мир  ";
    public string Output => "Привет,   мир  \r\n";
}

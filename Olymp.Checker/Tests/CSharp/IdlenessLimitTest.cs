namespace Olymp.Checker.Tests.CSharp;

public class IdlenessLimitTest : ICheckerTest
{
    public string Source => """
            System.Threading.Thread.Sleep(60_000);
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.IdlenessLimit;
}

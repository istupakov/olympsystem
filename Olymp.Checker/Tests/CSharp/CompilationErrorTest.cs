namespace Olymp.Checker.Tests.CSharp;

public class CompilationErrorTest : ICheckerTest
{
    public string Source => """
            System.Console.WriteLine(41
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.CompilationError;
}

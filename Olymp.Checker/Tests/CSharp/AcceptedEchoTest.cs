namespace Olymp.Checker.Tests.CSharp;

public class AcceptedEchoTest : ICheckerTest
{
    public string Source => """
            while (System.Console.ReadLine() is string line)
                System.Console.WriteLine(line);
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => """

        1 2 3
          x  
        xxxxxxxxxxxxx

        """;
    public string Output => Input;
}

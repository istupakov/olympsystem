namespace Olymp.Checker.Tests.Python;

public class MemoryLimitTest : ICheckerTest
{
    public string Source => """
            blocks = []
            for i in range(10000):
                blocks.append([0] * 1024**2)
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.MemoryLimit;
}

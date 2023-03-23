namespace Olymp.Checker.Tests.Python;

public class MemoryLimitTest : ICheckerTest
{
    public string Source => """
            list = []
            for i in range(10000):
                list.append([0] * 1024**2)
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.MemoryLimit;
}

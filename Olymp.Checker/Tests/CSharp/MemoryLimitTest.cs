namespace Olymp.Checker.Tests.CSharp;

public class MemoryLimitTest : ICheckerTest
{
    public string Source => """
            System.Collections.Generic.List<byte[]> list = new();
            for(int i=0; i<10000; ++i)
                list.Add(new byte[80*1024*1024]);
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.MemoryLimit;
}

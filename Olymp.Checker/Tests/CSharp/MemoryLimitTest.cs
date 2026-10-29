namespace Olymp.Checker.Tests.CSharp;

public class MemoryLimitTest : ICheckerTest
{
    public string Source => """
            System.Collections.Generic.List<byte[]> blocks = new();
            for(int i=0; i<10000; ++i)
            {
                byte[] block = new byte[8*1024*1024];
                for(int j=0; j<block.Length; j+=4096)
                    block[j] = 1;
                blocks.Add(block);
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.MemoryLimit;
}

namespace Olymp.Checker.Tests.Pascal;

public class MemoryLimitTest : ICheckerTest
{
    public string Source => """
            var blocks: array[0..9999] of pointer;
                i: longint;
            begin
              for i := 0 to High(blocks) do
              begin
                GetMem(blocks[i], 8*1024*1024);
                FillChar(blocks[i]^, 8*1024*1024, 1)
              end
            end.
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.MemoryLimit;
}

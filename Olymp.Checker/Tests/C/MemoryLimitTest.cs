namespace Olymp.Checker.Tests.C;

public class MemoryLimitTest : ICheckerTest
{
    public string Source => """
            #include <stdlib.h>
            int main() {
                const int blockSize = 8*1024*1024;
                volatile unsigned char *blocks[10000];
                for(int i=0; i<10000; ++i) {
                    blocks[i] = malloc(blockSize);
                    if (blocks[i] == NULL)
                        return 1;
                    for(int j=0; j<blockSize; j+=4096)
                        blocks[i][j] = 1;
                }
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.MemoryLimit;
}

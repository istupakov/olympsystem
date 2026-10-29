namespace Olymp.Checker.Tests.Cpp;

public class MemoryLimitTest : ICheckerTest
{
    public string Source => """
            #include <vector>
            int main() {
                std::vector<std::vector<unsigned char>> blocks;
                for(int i=0; i<10000; ++i)
                    blocks.push_back(std::vector<unsigned char>(8*1024*1024, 1));
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.MemoryLimit;
}

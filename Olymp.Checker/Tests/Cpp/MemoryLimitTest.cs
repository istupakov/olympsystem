namespace Olymp.Checker.Tests.Cpp;

public class MemoryLimitTest : ICheckerTest
{
    public string Source => """
            #include <vector>
            int main() {
                std::vector<std::vector<int>> vec;
                for(int i=0; i<10000; ++i)
                    vec.push_back(std::vector<int>(80*1024*1024));
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.MemoryLimit;
}

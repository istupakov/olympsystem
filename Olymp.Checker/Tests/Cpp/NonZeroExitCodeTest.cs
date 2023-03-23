namespace Olymp.Checker.Tests.Cpp;

public class NonZeroExitCodeTest : ICheckerTest
{
    public string Source => """
            int main() {
                return -1;
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.RuntimeError;
}

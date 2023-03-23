namespace Olymp.Checker.Tests.C;

public class CompilationErrorTest : ICheckerTest
{
    public string Source => """
            #include <stdio.h>
            int main() {
                printf("%d", 42
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.CompilationError;
}

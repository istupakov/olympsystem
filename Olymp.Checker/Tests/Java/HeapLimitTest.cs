namespace Olymp.Checker.Tests.Java;

public class HeapLimitTest : ICheckerTest
{
    public string Source => """
            public class Main {
              public static void main(String[] args) {
                byte[] block = new byte[129*1024*1024];
                System.out.println(block.length);
              }
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.RuntimeError;
}

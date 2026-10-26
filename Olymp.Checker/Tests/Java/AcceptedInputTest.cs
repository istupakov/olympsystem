namespace Olymp.Checker.Tests.Java;

public class AcceptedInputTest : ICheckerTest
{
    public string Source => """
            public class Main {
              public static void main(String[] args) {
                java.util.Scanner input = new java.util.Scanner(System.in);
                System.out.println(input.nextInt() + input.nextInt());
              }
            }
            """;
    public CheckerResultStatus ExpectedResult => CheckerResultStatus.Accepted;
    public string Input => "17\r\n25\r\n";
    public string Output => "42\r\n";
}

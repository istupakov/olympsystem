using Olymp.Checker;
using Olymp.Checker.Checkers;

using Xunit;

namespace Olymp.Tests.Checker;

public class DefaultCheckerTests
{
    [Theory]
    [InlineData("", "", CheckerResultStatus.Accepted)]
    [InlineData("", " \t\r\n", CheckerResultStatus.Accepted)]
    [InlineData("42", "42", CheckerResultStatus.Accepted)]
    [InlineData("42\r\n", "42", CheckerResultStatus.Accepted)]
    [InlineData("1 2\n3", " 1\t2\r\n3 \n", CheckerResultStatus.Accepted)]
    [InlineData("Привет, мир!", "Привет,\nмир!", CheckerResultStatus.Accepted)]
    [InlineData("42", "43", CheckerResultStatus.WrongAnswer)]
    [InlineData("42", "", CheckerResultStatus.WrongAnswer)]
    [InlineData("", "42", CheckerResultStatus.WrongAnswer)]
    [InlineData("1 2", "1 2 3", CheckerResultStatus.WrongAnswer)]
    [InlineData("1 2 3", "1 2", CheckerResultStatus.WrongAnswer)]
    [InlineData("1 2", "2 1", CheckerResultStatus.WrongAnswer)]
    [InlineData("Answer", "answer", CheckerResultStatus.WrongAnswer)]
    public void CheckComparesAnswersIgnoringWhitespace(string expectedOutput, string actualOutput,
        CheckerResultStatus expectedStatus)
    {
        var checker = new DefaultChecker();

        var status = checker.Check("", expectedOutput, actualOutput);

        Assert.Equal(expectedStatus, status);
    }
}

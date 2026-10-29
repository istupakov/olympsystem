using Olymp.Checker;
using Olymp.Checker.Checkers;

using Xunit;

namespace Olymp.Tests.Checker;

public class DefaultStrictCheckerTests
{
    [Theory]
    [InlineData("", "", CheckerResultStatus.Accepted)]
    [InlineData("42", "42", CheckerResultStatus.Accepted)]
    [InlineData("42\r\n", "42\n", CheckerResultStatus.Accepted)]
    [InlineData("42\n", "42", CheckerResultStatus.Accepted)]
    [InlineData("1\r\n2\r\n", "1\n2\n", CheckerResultStatus.Accepted)]
    [InlineData("Привет", "Привет", CheckerResultStatus.Accepted)]
    [InlineData("42", "43", CheckerResultStatus.WrongAnswer)]
    [InlineData("42", " 42", CheckerResultStatus.WrongAnswer)]
    [InlineData("42", "42 ", CheckerResultStatus.WrongAnswer)]
    [InlineData("1 2", "1\t2", CheckerResultStatus.WrongAnswer)]
    [InlineData("1\n2", "1\n3", CheckerResultStatus.WrongAnswer)]
    [InlineData("1\n2", "1", CheckerResultStatus.PresentationError)]
    [InlineData("1", "1\n2", CheckerResultStatus.PresentationError)]
    [InlineData("1\n2", "1\n\n2", CheckerResultStatus.WrongAnswer)]
    [InlineData("42\n", "42\n\n", CheckerResultStatus.PresentationError)]
    [InlineData("\n42\n", "42\n", CheckerResultStatus.WrongAnswer)]
    public void CheckPreservesWhitespaceAndLineStructure(string expectedOutput, string actualOutput,
        CheckerResultStatus expectedStatus)
    {
        var checker = new DefaultStrictChecker();

        var status = checker.Check("", expectedOutput, actualOutput);

        Assert.Equal(expectedStatus, status);
    }
}

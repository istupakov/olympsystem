using System.Globalization;

using Olymp.Checker;
using Olymp.Checker.Checkers;

using Xunit;

namespace Olymp.Tests.Checker;

public class FloatArrayCheckerTests
{
    [Theory]
    [InlineData("1.5", "1.5", CheckerResultStatus.Accepted)]
    [InlineData("0", "0.001", CheckerResultStatus.Accepted)]
    [InlineData("0", "-0.001", CheckerResultStatus.Accepted)]
    [InlineData("0", "0.0011", CheckerResultStatus.WrongAnswer)]
    [InlineData("0", "-0.0011", CheckerResultStatus.WrongAnswer)]
    [InlineData("1.5 -2.5\n3", " 1.5005  -2.5\n3.0005\n", CheckerResultStatus.Accepted)]
    [InlineData("1000", "1e3", CheckerResultStatus.Accepted)]
    [InlineData("1 2", "1", CheckerResultStatus.PresentationError)]
    [InlineData("1", "1 2", CheckerResultStatus.PresentationError)]
    [InlineData("1 2", "1\n2", CheckerResultStatus.PresentationError)]
    [InlineData("1", "not-a-number", CheckerResultStatus.PresentationError)]
    [InlineData("1", "", CheckerResultStatus.PresentationError)]
    [InlineData("1", "NaN", CheckerResultStatus.WrongAnswer)]
    [InlineData("1", "Infinity", CheckerResultStatus.WrongAnswer)]
    public void CheckUsesAbsoluteToleranceAndRejectsInvalidOutput(string expectedOutput, string actualOutput,
        CheckerResultStatus expectedStatus)
    {
        var checker = new FloatArrayChecker();

        var status = checker.Check("", expectedOutput, actualOutput);

        Assert.Equal(expectedStatus, status);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void CheckParsesDecimalPointRegardlessOfCurrentCulture(string cultureName)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            var checker = new FloatArrayChecker();

            var status = checker.Check("", "1.5", "1.5005");

            Assert.Equal(CheckerResultStatus.Accepted, status);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}

using Olymp.Checker;

using Xunit;

namespace Olymp.Tests.Checker;

public class CheckerResultStatusTests
{
    [Theory]
    [InlineData(CheckerResultStatus.Accepted, null, 2)]
    [InlineData(CheckerResultStatus.CompilationError, null, -1)]
    [InlineData(CheckerResultStatus.InternalError, null, -10)]
    [InlineData(CheckerResultStatus.InternalError, 42, -10)]
    [InlineData(CheckerResultStatus.WrongAnswer, 1, -1001)]
    [InlineData(CheckerResultStatus.TimeLimit, 1, -2001)]
    [InlineData(CheckerResultStatus.RuntimeError, 1, -3001)]
    [InlineData(CheckerResultStatus.PresentationError, 1, -4001)]
    [InlineData(CheckerResultStatus.IdlenessLimit, 1, -5001)]
    [InlineData(CheckerResultStatus.MemoryLimit, 1, -6001)]
    [InlineData(CheckerResultStatus.WrongAnswer, 999, -1999)]
    [InlineData(CheckerResultStatus.MemoryLimit, 999, -6999)]
    public void VerdictsKeepPersistedStatusCodes(CheckerResultStatus status, int? test, int expected)
    {
        Assert.Equal(expected, status.MakeStatusCode(test));
    }

    [Theory]
    [InlineData(CheckerResultStatus.WrongAnswer, null)]
    [InlineData(CheckerResultStatus.WrongAnswer, 0)]
    [InlineData(CheckerResultStatus.TimeLimit, -1)]
    [InlineData(CheckerResultStatus.MemoryLimit, 1000)]
    [InlineData(CheckerResultStatus.Accepted, 1)]
    [InlineData(CheckerResultStatus.CompilationError, 1)]
    public void InvalidVerdictAndTestCombinationsAreRejected(CheckerResultStatus status, int? test)
    {
        Assert.Throws<ArgumentException>(() => status.MakeStatusCode(test));
    }
}

using Olymp.Checker.Checkers;

using Xunit;

namespace Olymp.Tests.Checker.Checkers;

public class CheckerIdsTests
{
    [Fact]
    public void CheckerIdsRemainCompatibleWithStoredProblems()
    {
        Assert.Equal(1, new LegacyChecker().Id);
        Assert.Equal(2, new FloatArrayChecker().Id);
        Assert.Equal(3, new MuseumChecker().Id);
        Assert.Equal(4, new DefaultStrictChecker().Id);
    }
}

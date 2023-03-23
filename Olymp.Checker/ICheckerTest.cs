using Olymp.Checker.Checkers;

namespace Olymp.Checker;

public interface ICheckerTest
{
    string Name => GetType().Name;
    bool Optional => false;

    string Language => GetType().Namespace?.Split('.').LastOrDefault() switch
    {
        "CSharp" => "C#",
        "Cpp" => "C++",
        string lang and { Length: > 0 } => lang,
        _ => throw new InvalidOperationException()
    };

    string Source { get; }
    string Input => string.Empty;
    string Output => string.Empty;

    TimeSpan TimeLimit => TimeSpan.FromSeconds(1);
    int MemoryLimitMB => 128;

    ISimpleChecker Checker => new DefaultStrictChecker();

    CheckerResultStatus ExpectedResult { get; }
}

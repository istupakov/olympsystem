namespace Olymp.Checker;

public enum CheckerResultStatus
{
    Accepted,
    CompilationError,
    TimeLimit,
    IdlenessLimit,
    MemoryLimit,
    RuntimeError,
    PresentationError,
    WrongAnswer,
    InternalError,
}

public static class CheckerResultStatusExtensions
{
    public static int MakeStatusCode(this CheckerResultStatus status, int? test = null)
    {
        return (status, test) switch
        {
            (CheckerResultStatus.CompilationError, null) => -1,
            (CheckerResultStatus.Accepted, null) => 2,
            (CheckerResultStatus.InternalError, _) => -10,
            (_, null or <= 0 or >= 1000) => throw new ArgumentException("Invalid value", nameof(test)),
            (CheckerResultStatus.WrongAnswer, int t) => -1000 - t,
            (CheckerResultStatus.TimeLimit, int t) => -2000 - t,
            (CheckerResultStatus.RuntimeError, int t) => -3000 - t,
            (CheckerResultStatus.PresentationError, int t) => -4000 - t,
            (CheckerResultStatus.IdlenessLimit, int t) => -5000 - t,
            (CheckerResultStatus.MemoryLimit, int t) => -6000 - t,
            _ => throw new ArgumentException("Invalid value", nameof(status))
        };
    }
}

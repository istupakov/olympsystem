namespace Olymp.Checker;

public interface ISimpleChecker
{
    int Id { get; }
    CheckerResultStatus Check(string juryInput, string juryOutput, string userOutput);
}

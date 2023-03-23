namespace Olymp.Checker.Checkers;

public class DefaultChecker : ISimpleChecker
{
    public int Id => 1;

    public CheckerResultStatus Check(string juryInput, string juryOutput, string userOutput)
    {
        int i = 0, j = 0;
        while (true)
        {
            while (i < userOutput.Length && char.IsWhiteSpace(userOutput[i])) i++;
            while (j < juryOutput.Length && char.IsWhiteSpace(juryOutput[j])) j++;

            if (i == userOutput.Length && j == juryOutput.Length)
                return CheckerResultStatus.Accepted;
            if (i == userOutput.Length || j == juryOutput.Length || userOutput[i++] != juryOutput[j++])
                return CheckerResultStatus.WrongAnswer;
        }
    }
}

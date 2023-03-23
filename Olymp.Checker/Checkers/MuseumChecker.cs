namespace Olymp.Checker.Checkers;

public class MuseumChecker : ISimpleChecker
{
    public int Id => 3;

    private static IEnumerable<int> SelectColumn(List<int[]> data, int k)
    {
        for (int i = 0; i < data.Count; i++)
            yield return data[i][k];
    }

    private static CheckerResultStatus CheckInternal(string juryInput, string userOutput)
    {
        var jline = juryInput.Split(' ').Select(v => Convert.ToInt32(v)).ToArray();
        var n = jline[0];
        var m = jline[1];

        var data = new List<int[]>();
        using (var reader = new StringReader(userOutput))
        {
            while (true)
            {
                var line = reader.ReadLine();
                if (line == null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (!string.IsNullOrWhiteSpace(reader.ReadToEnd()))
                        return CheckerResultStatus.PresentationError;
                    break;
                }

                var arr = line.TrimEnd(' ').Split(' ').Select(v => Convert.ToInt32(v)).ToArray();

                if (arr.Length != n)
                    return CheckerResultStatus.PresentationError;
                if (arr.Any(v => v < 0 || v > m))
                    return CheckerResultStatus.WrongAnswer;

                data.Add(arr);
            }
        }

        if (data.Count == 0)
            return CheckerResultStatus.PresentationError;

        if (data.Count != Math.Max(n, m))
            return CheckerResultStatus.WrongAnswer;

        for (int i = 0; i < data.Count; i++)
        {
            var nonzero = data[i].Where(v => v > 0);
            if (nonzero.Count() != nonzero.Distinct().Count())
                return CheckerResultStatus.WrongAnswer;
        }

        for (int i = 0; i < n; i++)
        {
            var nonzero = SelectColumn(data, i).Where(v => v > 0);
            var count = nonzero.Count();
            if (count != nonzero.Distinct().Count() || count != m)
                return CheckerResultStatus.WrongAnswer;
        }

        return CheckerResultStatus.Accepted;
    }

    public CheckerResultStatus Check(string juryInput, string juryOutput, string userOutput)
    {
        try
        {
            return CheckInternal(juryInput, userOutput);
        }
        catch
        {
            return CheckerResultStatus.PresentationError;
        }
    }
}

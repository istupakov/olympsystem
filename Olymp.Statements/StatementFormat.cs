namespace Olymp.Statements;

public static class StatementFormat
{
    public static readonly DateTimeOffset Cutoff = new(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(7));

    public static bool UsesMarkdown(DateTimeOffset contestStart) => contestStart >= Cutoff;
}

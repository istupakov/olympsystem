namespace Olymp.WebApp.Components.Problems;

public sealed record ProblemExample(string Input, string Output);

public sealed record ProblemStatement(int ContestId,
    int TimeLimit, int MemoryLimit, string? Html, bool HasPdf, IReadOnlyList<ProblemExample> Examples);

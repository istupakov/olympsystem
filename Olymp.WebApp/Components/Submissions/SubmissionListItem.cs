namespace Olymp.WebApp.Components.Submissions;

public sealed record SubmissionListItem(int Id, DateTimeOffset CommitTime, string Author, bool IsParticipant,
    string Compiler, int ContestId, string Contest, int ProblemId, string Problem,
    bool IsSchool, int? StatusCode, bool? IsAccepted, int? Score, bool CanViewSource)
{
    public string? CompilerDescription { get; init; }
}

namespace Olymp.WebApp.Components.Staff;

public sealed record StaffSubmissionListItem
{
    public int Id { get; init; }
    public DateTimeOffset CommitTime { get; init; }
    public required string Author { get; init; }
    public int AuthorId { get; init; }
    public int? AuthorContestId { get; init; }
    public bool IsParticipant { get; init; }
    public bool IsTeam { get; init; }
    public required string Compiler { get; init; }
    public int CompilerId { get; init; }
    public string? Language { get; init; }
    public int ContestId { get; init; }
    public required string Contest { get; init; }
    public int ProblemId { get; init; }
    public required string Problem { get; init; }
    public int StatusCode { get; init; }
    public int? Score { get; init; }
    public string? IpAddress { get; init; }
    public bool IsHidden { get; init; }
    public bool IsReference { get; init; }
}

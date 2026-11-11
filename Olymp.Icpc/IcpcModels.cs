using System.Text.Json.Serialization;

namespace Olymp.Icpc;

public sealed record IcpcRegion
{
    public long Id { get; init; }
    public string Label { get; init; } = "";
}

public sealed record IcpcContest
{
    public long Id { get; init; }
    [JsonPropertyName("contest")]
    public string Name { get; init; } = "";
    public string? Date { get; init; }
}

public sealed record IcpcMember
{
    public long TeamId { get; init; }
    public string? Role { get; init; }
    public string? Username { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }

    [JsonIgnore]
    public string Name => string.Join(" ", new[] { FirstName, LastName }.Where(value => !string.IsNullOrWhiteSpace(value)));
    [JsonIgnore]
    public bool IsContestant => Role is "CONTESTANT" or "STUDENT_COACH" or "CONTESTANT_COACH";
    [JsonIgnore]
    public bool IsCoach => Role is "COACH" or "STUDENT_COACH" or "CONTESTANT_COACH";
}

public sealed record IcpcTeam
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string? Status { get; init; }
    public string? InstName { get; init; }
    public string? EligibilityStatus { get; init; }
    public string? EligibilityIssue { get; init; }
    [JsonIgnore]
    public IReadOnlyList<IcpcMember> Members { get; init; } = [];
}

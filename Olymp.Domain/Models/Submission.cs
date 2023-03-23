namespace Olymp.Domain.Models;

public partial class Submission
{
    public virtual int Id { get; set; }

    public virtual required byte[] SourceCode { get; set; }

    public virtual int StatusCode { get; set; }

    public virtual bool IsHidden { get; set; }

    public virtual bool IsReference { get; set; }

    public virtual string? Description { get; set; }

    public virtual DateTimeOffset CommitTime { get; set; }

    public virtual DateTimeOffset LastModification { get; set; }

    public virtual string? CommiterInfo { get; set; }

    public virtual int? Score { get; set; }

    public virtual string? ScoresByTest { get; set; }

    public virtual int UserId { get; set; }

    public virtual required User User { get; set; }

    public virtual int ProblemId { get; set; }

    public virtual required Problem Problem { get; set; }

    public virtual int CompilatorId { get; set; }

    public virtual required Compilator Compilator { get; set; }

    public virtual required ICollection<CheckResult> CheckResults { get; init; }
}

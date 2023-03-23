namespace Olymp.Domain.Models;

public partial class CheckResult
{
    public virtual int Id { get; set; }

    public virtual required DateTimeOffset CheckTime { get; set; }

    public virtual required int StatusCode { get; set; }

    public virtual required string Log { get; set; }

    public virtual int? Score { get; set; }

    public virtual string? TestResults { get; set; }

    public virtual int SubmissionId { get; set; }

    public virtual required Submission Submission { get; set; }
}

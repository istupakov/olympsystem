namespace Olymp.Domain.Models;

public partial class Message
{
    public virtual int Id { get; set; }

    public virtual string? UserText { get; set; }

    public virtual string? JuryText { get; set; }

    public virtual DateTimeOffset? SendTime { get; set; }

    public virtual int? UserId { get; set; }

    public virtual User? User { get; set; }

    public virtual int? ProblemId { get; set; }

    public virtual Problem? Problem { get; set; }

    public virtual int ContestId { get; set; }

    public virtual required Contest Contest { get; set; }
}

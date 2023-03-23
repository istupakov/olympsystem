namespace Olymp.Domain.Models;

public partial class Problem
{
    public virtual int Id { get; set; }

    public virtual int Number { get; set; }

    public virtual required string Name { get; set; }

    public virtual string? Text { get; set; }

    public virtual bool IsActive { get; set; }

    public virtual int TimeLimit { get; set; }

    public virtual int SlowTimeLimit { get; set; }

    public virtual int MemoryLimit { get; set; }

    public virtual int ContestId { get; set; }

    public virtual required Contest Contest { get; set; }

    public virtual int CheckerId { get; set; }

    public virtual required Checker Checker { get; set; }

    public virtual required ICollection<Message> Messages { get; init; }

    public virtual required ICollection<Submission> Submissions { get; init; }

    public virtual required ICollection<Test> Tests { get; init; }
}

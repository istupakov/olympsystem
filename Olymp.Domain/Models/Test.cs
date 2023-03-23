namespace Olymp.Domain.Models;

public partial class Test
{
    public virtual int Id { get; set; }

    public virtual int Number { get; set; }

    public virtual required byte[] Input { get; set; }

    public virtual required byte[] Output { get; set; }

    public virtual bool IsOpen { get; set; }

    public virtual bool IsActive { get; set; }

    public virtual int? Score { get; set; }

    public virtual string? Comment { get; set; }

    public virtual int ProblemId { get; set; }

    public virtual required Problem Problem { get; set; }
}

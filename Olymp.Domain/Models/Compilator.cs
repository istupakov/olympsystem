namespace Olymp.Domain.Models;

public partial class Compilator
{
    public virtual int Id { get; set; }

    public virtual required string Language { get; set; }

    public virtual required string Name { get; set; }

    public virtual string? Description { get; set; }

    public virtual required string SourceExtension { get; set; }

    public virtual required string CommandLine { get; set; }

    public virtual required string ConfigName { get; set; }

    public virtual bool IsActive { get; set; }

    public virtual required ICollection<Submission> Submissions { get; init; }
}

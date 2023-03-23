namespace Olymp.Domain.Models;

public partial class Checker
{
    public virtual int Id { get; set; }

    public virtual required string Description { get; set; }

    public virtual required ICollection<Problem> Problems { get; init; }
}

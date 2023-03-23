namespace Olymp.Domain.Models;

public partial class Organization
{
    public virtual int Id { get; set; }

    public virtual required string Name { get; set; }

    public virtual string? FullName { get; set; }

    public virtual string? ACMName { get; set; }

    public virtual required ICollection<User> Users { get; init; }
}

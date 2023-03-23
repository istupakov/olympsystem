namespace Olymp.Domain.Models;

public partial class OlympUser : User
{
    public virtual string? FirstName { get; set; }

    public virtual string? LastName { get; set; }

    public virtual string? Patronymic { get; set; }

    public virtual int? Year { get; set; }

    public virtual string? Details { get; set; }

    public virtual required ICollection<Competitor> Memberships { get; init; }

    public virtual required ICollection<User> Teams { get; init; }
}

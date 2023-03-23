namespace Olymp.Domain.Models;

public partial class Competitor : User
{
    public virtual bool IsApproved { get; set; }

    public virtual bool IsOutOfCompetition { get; set; }

    public virtual string? MemberInfos { get; set; }

    public virtual int ContestId { get; set; }

    public virtual required Contest Contest { get; set; }

    public virtual required ICollection<OlympUser> Members { get; init; }
}

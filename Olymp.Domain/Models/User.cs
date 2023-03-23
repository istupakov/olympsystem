using Microsoft.AspNetCore.Identity;

namespace Olymp.Domain.Models;

public abstract partial class User : IdentityUser<int>
{
    public virtual required string Name { get; set; }

    public virtual bool IsDisqualified { get; set; }

    public virtual bool IsHidden { get; set; }

    public virtual DateTimeOffset? RegistrationDate { get; set; }

    public virtual int? CoachId { get; set; }

    public virtual OlympUser? Coach { get; set; }

    public virtual int? OrganizationId { get; set; }

    public virtual Organization? Organization { get; set; }

    public virtual required ICollection<Message> Messages { get; init; }

    public virtual required ICollection<Submission> Submissions { get; init; }
}

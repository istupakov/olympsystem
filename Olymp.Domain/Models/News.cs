namespace Olymp.Domain.Models;

public partial class News
{
    public virtual int Id { get; set; }

    public virtual required string Title { get; set; }

    public virtual required string Text { get; set; }

    public virtual DateTimeOffset PublicationDate { get; set; }
}

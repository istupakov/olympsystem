namespace Olymp.Domain.Models;

public partial class Blob
{
    public virtual int Id { get; set; }

    public virtual required byte[] Data { get; set; }
}

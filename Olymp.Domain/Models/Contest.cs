namespace Olymp.Domain.Models;

public partial class Contest
{
    public virtual int Id { get; set; }

    public virtual required string Name { get; set; }

    public virtual required string Abbr { get; set; }

    public virtual DateTimeOffset StartTime { get; set; }

    public virtual DateTimeOffset EndTime { get; set; }

    public virtual bool IsOpen { get; set; }

    public virtual bool IsHidden { get; set; }

    public virtual string? Description { get; set; }

    public virtual DateTimeOffset? ProblemShowTime { get; set; }

    public virtual DateTimeOffset? FreezeTime { get; set; }

    public virtual int? ProblemPdfId { get; set; }

    public virtual Blob? ProblemPdf { get; set; }

    public virtual int? TestZipId { get; set; }

    public virtual Blob? TestZip { get; set; }

    public virtual int? FinalTableId { get; set; }

    public virtual Blob? FinalTable { get; set; }

    public virtual int? OfficialTableId { get; set; }

    public virtual Blob? OfficialTable { get; set; }

    public virtual required ICollection<Message> Messages { get; init; }

    public virtual required ICollection<Problem> Problems { get; init; }

    public virtual required ICollection<Competitor> Competitors { get; init; }
}

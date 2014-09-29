using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace OlympSystem.Models
{
    public class Contest
    {
        public int Id { get; set; }

        public string Name { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }
        public string Abbr { get; set; }
        public bool Open { get; set; }
        public bool Hidden { get; set; }

        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public DateTime? TaskShowTime { get; set; }
        public DateTime? FreezeTime { get; set; }

        public int? TaskPdfId { get; set; }
        public virtual BinaryData TaskPdf { get; set; }

        public int? TestZipId { get; set; }
        public virtual BinaryData TestZip { get; set; }        
       
        public int? FinalTableId { get; set; }
        public virtual BinaryData FinalTable { get; set; }

        public int? OfficialTableId { get; set; }
        public virtual BinaryData OfficialTable { get; set; }

        public virtual ICollection<Problem> Problems { get; set; }
        public virtual ICollection<Clarification> Messages { get; set; }
        public virtual ICollection<Competitor> Competitors { get; set; }
    }
}
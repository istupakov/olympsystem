using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace OlympSystem.Models
{
    public class Clarification
    {
        public int Id { get; set; }
        
        public string Text { get; set; }
        public DateTime? PublishTime { get; set; }

        public int? ContestId { get; set; }
        public virtual Contest Contest { get; set; }

        public int? ProblemId { get; set; }
        public virtual Problem Problem { get; set; }
    }

    public class ClarificationToUser: Clarification
    {
        public string QuestionText { get; set; }
        public DateTime? QuestionTime { get; set; }

        public int UserId { get; set; }
        public virtual User User { get; set; }
    }
}
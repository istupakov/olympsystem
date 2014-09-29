using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Web;

namespace OlympSystem.Models
{
    public class User
    {
        public int Id { get; set; }
        public string Login { get; set; }
        public string Password { get; set; }
        public bool IsHidden { get; set; }
        public string Name { get; set; }
        public string Info { get; set; }
        public DateTime? RegistrationDate { get; set; }

        [ForeignKey("Coach")]
        public int? CoachId { get; set; }
        public virtual User Coach { get; set; }

        public int? SchoolId { get; set; }
        public virtual School School { get; set; }

        public virtual ICollection<Competitor> Memberships { get; set; }
        public virtual ICollection<Solution> Solutions { get; set; }
        public virtual ICollection<ClarificationToUser> Clarifications { get; set; }
    }

    public class Competitor : User
    {
        public int ContestId { get; set; }
        public virtual Contest Contest { get; set; }

        public bool IsApproved { get; set; }        
        public bool IsDisqualify { get; set; }
        public bool IsNonOfficial { get; set; }

        public virtual ICollection<User> Members { get; set; }
    }
}
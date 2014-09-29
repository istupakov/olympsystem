using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace OlympSystem.Models
{
    public class School
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string NameEng { get; set; }
        public string FullName { get; set; }
        public string FullNameEng { get; set; }
        public string DefaultTeamName { get; set; }
        public string Tags { get; set; }

        public virtual ICollection<User> Users { get; set; }
    }
}
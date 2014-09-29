using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Web;

namespace OlympSystem.Models
{
    public class Problem
    {
        public Problem()
        {
            this.Name = "Новая задача";
            this.TimeLimit = 1;
            this.SlowTimeLimit = 1;
            this.MemoryLimit = 256;
            this.OpenTestNumber = 1;
        }

        public int Id { get; set; }
        public int Number { get; set; }
        public string Name { get; set; }

        public bool Public { get; set; }
        public bool Active { get; set; }
        public int OpenTestNumber { get; set; }
        public double TimeLimit { get; set; }
        public double SlowTimeLimit { get; set; }
        public int MemoryLimit { get; set; }

        [Display(Name = "Условие"), DataType(DataType.Html)]
        public string Text { get; set; }

        [Display(Name = "Теги")]
        public string Tags { get; set; }

        [Display(Name = "Название")]
        public string FullName
        {
            get
            {
                var format = Contest.Problems.Count > 9 ? "{0:00}. {1}" : "{0}. {1}";
                return string.Format(format, Number, Name);
            }
        }

        public int? ContestId { get; set; }
        public virtual Contest Contest { get; set; }

        [Display(Name = "Олимпиада")]
        public string ContestName { get { return Contest.Name; } }

        public virtual ICollection<Test> Tests { get; set; }
        public virtual ICollection<Solution> Solutions { get; set; }
        public virtual ICollection<Clarification> Clarifications { get; set; }
    }
}
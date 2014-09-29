using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace OlympSystem.Models
{
    public class Test
    {
        public int Id { get; set; }
        
        public int Number { get; set; }
        public int? Score { get; set; }
        public bool IsActive { get; set; }
        public string Comment { get; set; }        

        public virtual byte[] Input { get; set; }
        public virtual byte[] Output { get; set; }      
                
        public int ProblemId { get; set; }
        public virtual Problem Problem { get; set; }
    }
}
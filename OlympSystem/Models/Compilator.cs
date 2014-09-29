using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace OlympSystem.Models
{
    public class Compilator
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public string Language { get; set; }
        public string SourceExtension { get; set; }                
        public string CommandLine { get; set; }
        public string ConfigName { get; set; }

        public bool IsActive { get; set; }
    }
}
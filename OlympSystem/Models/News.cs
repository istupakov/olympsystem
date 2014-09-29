using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace OlympSystem.Models
{
    public class News
    {
        public int Id { get; set; }

        [Required]        
        [Display(Name = "Заголовок")]
        public string Title { get; set; }

        [Required]
        [Display(Name = "Текст")]
        [DataType(DataType.Html)]
        public string Text { get; set; }

        [Display(Name = "Дата публикации")]
        public DateTime PublicationDate { get; set; }
    }
}
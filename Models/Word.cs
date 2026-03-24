using System.ComponentModel.DataAnnotations;

namespace WordGuessAPI.Models;

public class Word
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string Text { get; set; } = string.Empty;

    [Required]
    public string Difficulty { get; set; } = "medium"; // easy, medium, hard

    public DateTime? Date { get; set; } // palabra del día
}
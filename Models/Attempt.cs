using System.ComponentModel.DataAnnotations;

namespace WordGuessAPI.Models;

public class Attempt
{
    [Key]
    public int Id { get; set; }

    public int GameId { get; set; }
    public Game? Game { get; set; }

    [Required, MaxLength(50)]
    public string Guess { get; set; } = string.Empty;

    [Required]
    public string Result { get; set; } = string.Empty; // JSON con evaluación

    public DateTime AttemptedAt { get; set; } = DateTime.UtcNow;
}
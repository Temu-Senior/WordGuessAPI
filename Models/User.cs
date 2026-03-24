using System.ComponentModel.DataAnnotations;

namespace WordGuessAPI.Models;

public class User
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(20)]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string PasswordHash { get; set; } = string.Empty;

    public bool IsAdmin { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Game> Games { get; set; } = new List<Game>();
}
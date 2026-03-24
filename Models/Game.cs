using System.ComponentModel.DataAnnotations;

namespace WordGuessAPI.Models;

public class Game
{
    [Key]
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public int WordId { get; set; }
    public Word? Word { get; set; }

    public bool IsCompleted { get; set; } = false;
    public bool IsWon { get; set; } = false;
    public int Attempts { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Attempt> AttemptsList { get; set; } = new List<Attempt>();
}
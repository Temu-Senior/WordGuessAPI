using System.Collections.Concurrent;

namespace WordGuessAPI.Models;

public class PlayerInRoom
{
    public string ConnectionId { get; set; }
    public string Name { get; set; }
    public int AttemptsLeft { get; set; } = 6;
    public string Status { get; set; } = "alive"; // alive, eliminated, winner
    public List<AttemptInfo> Attempts { get; set; } = new();
}

public class AttemptInfo
{
    public string Guess { get; set; }
    public List<LetterResult> Result { get; set; }
}

public class LetterResult
{
    public char Letter { get; set; }
    public string Status { get; set; } // correct, present, absent
}

public class Room
{
    public string Code { get; set; }
    public string CurrentWord { get; set; }
    public int WordLength { get; set; }
    public bool RoundActive { get; set; }
    public DateTime RoundStartTime { get; set; }
    public int TimeLimitSeconds { get; set; } = 60;
    public ConcurrentDictionary<string, PlayerInRoom> Players { get; set; } = new();
    public string Winner { get; set; }
    public Timer RoundTimer { get; set; }
}
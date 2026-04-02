using System.Collections.Concurrent;

namespace WordGuessAPI.Hubs;

public class PlayerInRoom
{
    public string ConnectionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int AttemptsLeft { get; set; } = 6;
    public string Status { get; set; } = "alive";
    public bool HasGuessedInRound { get; set; } = false;
    public List<AttemptInfo> Attempts { get; set; } = new();
}

public class Room
{
    public string Code { get; set; } = string.Empty;
    public string CurrentWord { get; set; } = string.Empty;
    public int WordLength { get; set; }
    public string Difficulty { get; set; } = "normal";
    public bool RoundActive { get; set; }
    public bool CountdownActive { get; set; }
    public ConcurrentDictionary<string, PlayerInRoom> Players { get; set; } = new();
}

public class AttemptInfo
{
    public string Guess { get; set; } = string.Empty;
    public List<LetterResult> Result { get; set; } = new();
}

public class LetterResult
{
    public char Letter { get; set; }
    public string Status { get; set; } = string.Empty;
}
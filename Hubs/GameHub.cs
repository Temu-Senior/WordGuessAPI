using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace WordGuessAPI.Hubs;

// ==================== CLASES AUXILIARES (dentro del mismo archivo) ====================
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

// ==================== HUB PRINCIPAL ====================
public class GameHub : Hub
{
    private static ConcurrentDictionary<string, Room> _rooms = new();
    private readonly IHubContext<GameHub> _hubContext;

    public GameHub(IHubContext<GameHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task CreateRoom(string roomCode, string difficulty, string playerName)
    {
        Console.WriteLine($"CreateRoom: {roomCode}, difficulty: {difficulty}, player: {playerName}");
        if (_rooms.ContainsKey(roomCode))
        {
            await Clients.Caller.SendAsync("Error", "La sala ya existe");
            return;
        }
        var wordLength = difficulty switch { "easy" => 4, "hard" => 6, _ => 5 };
        var room = new Room
        {
            Code = roomCode,
            Difficulty = difficulty,
            WordLength = wordLength,
            RoundActive = false,
            CountdownActive = false
        };
        _rooms.TryAdd(roomCode, room);
        await JoinRoom(roomCode, playerName);
    }

    public async Task JoinRoom(string roomCode, string playerName)
    {
        Console.WriteLine($"JoinRoom: {roomCode}, player: {playerName}, conn: {Context.ConnectionId}");
        if (!_rooms.TryGetValue(roomCode, out var room))
        {
            await Clients.Caller.SendAsync("Error", "La sala no existe");
            return;
        }
        var player = new PlayerInRoom
        {
            ConnectionId = Context.ConnectionId,
            Name = playerName,
            Status = "alive"
        };
        room.Players.TryAdd(Context.ConnectionId, player);
        await Groups.AddToGroupAsync(Context.ConnectionId, roomCode);
        
        await _hubContext.Clients.Group(roomCode).SendAsync("PlayersUpdate", GetPlayersList(room));
        await _hubContext.Clients.Group(roomCode).SendAsync("WaitingForPlayers", room.Players.Count);
        
        Console.WriteLine($"Players in room {roomCode}: {room.Players.Count}");
        
        // Si hay al menos 2 jugadores y no hay ronda activa, iniciar countdown
        if (room.Players.Count >= 2 && !room.RoundActive)
        {
            // Resetear por si quedó trabado
            room.CountdownActive = false;
            Console.WriteLine("Iniciando countdown...");
            _ = Task.Run(() => StartCountdown(roomCode));
        }
    }

    public async Task LeaveRoom(string roomCode)
    {
        if (_rooms.TryGetValue(roomCode, out var room))
        {
            room.Players.TryRemove(Context.ConnectionId, out _);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomCode);
            await _hubContext.Clients.Group(roomCode).SendAsync("PlayersUpdate", GetPlayersList(room));
            if (room.Players.IsEmpty)
                _rooms.TryRemove(roomCode, out _);
            else
                await _hubContext.Clients.Group(roomCode).SendAsync("WaitingForPlayers", room.Players.Count);
        }
    }

    private async Task StartCountdown(string roomCode)
    {
        Console.WriteLine($"StartCountdown called for {roomCode}");
        if (!_rooms.TryGetValue(roomCode, out var room)) return;
        if (room.CountdownActive) { Console.WriteLine("Countdown already active"); return; }
        if (room.RoundActive) { Console.WriteLine("Round already active"); return; }
        room.CountdownActive = true;
        Console.WriteLine($"Countdown activated for {roomCode}, players: {room.Players.Count}");

        try
        {
            // Enviar mensaje de prueba para verificar el grupo
            await _hubContext.Clients.Group(roomCode).SendAsync("TestMessage", "Iniciando countdown");
            Console.WriteLine("Test message sent to group");

            for (int i = 5; i >= 0; i--)
            {
                if (i == 0)
                {
                    Console.WriteLine("Starting round...");
                    await StartRound(roomCode);
                    return;
                }
                Console.WriteLine($"Sending tick {i} to group {roomCode}");
                await _hubContext.Clients.Group(roomCode).SendAsync("CountdownTick", i);
                await Task.Delay(1000);
                if (!_rooms.ContainsKey(roomCode)) return;
                room = _rooms[roomCode];
                if (room.RoundActive) return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in countdown: {ex.Message}");
        }
        finally
        {
            if (_rooms.TryGetValue(roomCode, out var r)) r.CountdownActive = false;
            Console.WriteLine($"Countdown finished for {roomCode}");
        }
    }

    private async Task StartRound(string roomCode)
    {
        Console.WriteLine($"StartRound for {roomCode}");
        if (!_rooms.TryGetValue(roomCode, out var room)) return;
        if (room.RoundActive) return;

        var word = GetWordByLength(room.WordLength);
        room.CurrentWord = word.ToUpper();
        room.RoundActive = true;

        foreach (var p in room.Players.Values)
        {
            if (p.Status != "eliminated")
            {
                p.AttemptsLeft = 6;
                p.HasGuessedInRound = false;
                p.Attempts.Clear();
                p.Status = "alive";
            }
        }

        await _hubContext.Clients.Group(roomCode).SendAsync("RoundStarted", room.WordLength);
        Console.WriteLine($"Round started with word: {room.CurrentWord}");
    }

    public async Task MakeGuess(string roomCode, string guess, string playerName)
    {
        if (!_rooms.TryGetValue(roomCode, out var room) || !room.RoundActive)
        {
            await Clients.Caller.SendAsync("Error", "No hay ronda activa");
            return;
        }
        var player = room.Players.Values.FirstOrDefault(p => p.Name == playerName);
        if (player == null || player.Status != "alive")
        {
            await Clients.Caller.SendAsync("Error", "No estás en la sala o ya fuiste eliminado");
            return;
        }
        if (player.HasGuessedInRound)
        {
            await Clients.Caller.SendAsync("Error", "Ya adivinaste esta ronda");
            return;
        }
        if (guess.Length != room.WordLength)
        {
            await Clients.Caller.SendAsync("Error", $"La palabra debe tener {room.WordLength} letras");
            return;
        }

        var result = EvaluateGuess(guess, room.CurrentWord);
        player.Attempts.Add(new AttemptInfo { Guess = guess, Result = result });
        player.AttemptsLeft--;

        var isCorrect = guess.ToUpper() == room.CurrentWord;
        if (isCorrect)
        {
            player.HasGuessedInRound = true;
        }
        else if (player.AttemptsLeft <= 0)
        {
            player.Status = "eliminated";
            await _hubContext.Clients.Group(roomCode).SendAsync("PlayerEliminated", player.Name);
        }

        await Clients.Caller.SendAsync("GuessResult", new
        {
            success = true,
            guess,
            resultArray = result,
            attemptsLeft = player.AttemptsLeft,
            gameCompleted = (isCorrect || player.Status == "eliminated"),
            won = isCorrect,
            word = isCorrect ? room.CurrentWord : null
        });

        await _hubContext.Clients.Group(roomCode).SendAsync("PlayersUpdate", GetPlayersList(room));

        var allAliveGuessed = room.Players.Values
            .Where(p => p.Status == "alive")
            .All(p => p.HasGuessedInRound);
        if (allAliveGuessed && room.Players.Values.Any(p => p.Status == "alive"))
        {
            await EndRound(roomCode);
        }
    }

    private async Task EndRound(string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room)) return;
        if (!room.RoundActive) return;
        room.RoundActive = false;

        var survivors = room.Players.Values.Where(p => p.Status == "alive" && p.HasGuessedInRound).ToList();
        var eliminated = room.Players.Values.Where(p => p.Status == "alive" && !p.HasGuessedInRound).ToList();

        foreach (var p in eliminated)
        {
            p.Status = "eliminated";
            await _hubContext.Clients.Group(roomCode).SendAsync("PlayerEliminated", p.Name);
        }

        if (survivors.Count == 1)
        {
            await _hubContext.Clients.Group(roomCode).SendAsync("GameEnded", survivors.First().Name);
            _rooms.TryRemove(roomCode, out _);
        }
        else if (survivors.Count == 0)
        {
            await _hubContext.Clients.Group(roomCode).SendAsync("GameEnded", null);
            _rooms.TryRemove(roomCode, out _);
        }
        else
        {
            await _hubContext.Clients.Group(roomCode).SendAsync("RoundEnded", survivors.Select(s => s.Name).ToList(), room.CurrentWord);
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                if (_rooms.ContainsKey(roomCode))
                    await StartCountdown(roomCode);
            });
        }
    }

    // ==================== MÉTODOS AUXILIARES ====================
    private List<LetterResult> EvaluateGuess(string guess, string target)
    {
        guess = guess.ToUpper();
        target = target.ToUpper();
        var result = new List<LetterResult>();
        var targetCount = target.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        for (int i = 0; i < guess.Length; i++)
        {
            char c = guess[i];
            if (c == target[i])
            {
                result.Add(new LetterResult { Letter = c, Status = "correct" });
                targetCount[c]--;
            }
            else result.Add(null);
        }
        for (int i = 0; i < guess.Length; i++)
        {
            if (result[i] != null) continue;
            char c = guess[i];
            if (targetCount.ContainsKey(c) && targetCount[c] > 0)
            {
                result[i] = new LetterResult { Letter = c, Status = "present" };
                targetCount[c]--;
            }
            else result[i] = new LetterResult { Letter = c, Status = "absent" };
        }
        return result;
    }

    private string GetWordByLength(int length)
    {
        var words = new Dictionary<int, List<string>>
        {
            { 4, new List<string> { "CASA", "GATO", "LUNA", "SOL", "RICO" } },
            { 5, new List<string> { "MUNDO", "RATON", "SILLA", "PERRO", "MESA" } },
            { 6, new List<string> { "PROBAR", "SERVID", "CLIENT", "RONDAS" } }
        };
        var list = words.ContainsKey(length) ? words[length] : words[5];
        return list[new Random().Next(list.Count)];
    }

    private object GetPlayersList(Room room)
    {
        return room.Players.Values.Select(p => new
        {
            p.Name,
            p.AttemptsLeft,
            p.Status,
            HasGuessed = p.HasGuessedInRound
        });
    }
}
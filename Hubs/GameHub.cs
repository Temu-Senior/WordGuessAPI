using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace WordGuessAPI.Hubs;

//[Authorize]
public class GameHub : Hub
{
    private static ConcurrentDictionary<string, Room> _rooms = new();

    public async Task CreateRoom(string roomCode, string difficulty = "normal")
    {
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
        await JoinRoom(roomCode);
    }

    public async Task JoinRoom(string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
        {
            await Clients.Caller.SendAsync("Error", "La sala no existe");
            return;
        }
        var username = Context.User?.Identity?.Name ?? Context.UserIdentifier;
        var player = new PlayerInRoom
        {
            ConnectionId = Context.ConnectionId,
            Name = username,
            Status = "alive"
        };
        room.Players.TryAdd(Context.ConnectionId, player);
        await Groups.AddToGroupAsync(Context.ConnectionId, roomCode);
        await Clients.Group(roomCode).SendAsync("PlayersUpdate", GetPlayersList(room));

        // Notificar a todos el número de jugadores
        await Clients.Group(roomCode).SendAsync("WaitingForPlayers", room.Players.Count);

        // Si hay al menos 2 jugadores y no hay ronda activa ni countdown activo, iniciar countdown
        if (room.Players.Count >= 2 && !room.RoundActive && !room.CountdownActive)
        {
            _ = Task.Run(() => StartCountdown(roomCode));
        }
    }

    public async Task LeaveRoom(string roomCode)
    {
        if (_rooms.TryGetValue(roomCode, out var room))
        {
            room.Players.TryRemove(Context.ConnectionId, out _);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomCode);
            await Clients.Group(roomCode).SendAsync("PlayersUpdate", GetPlayersList(room));
            if (room.Players.IsEmpty)
                _rooms.TryRemove(roomCode, out _);
            else
                await Clients.Group(roomCode).SendAsync("WaitingForPlayers", room.Players.Count);
        }
    }

    private async Task StartCountdown(string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room)) return;
        if (room.CountdownActive) return;
        room.CountdownActive = true;

        try
        {
            // Esperar un poco para asegurar que al menos 2 jugadores (si ya lo están, no espera)
            while (room.Players.Count < 2)
            {
                await Task.Delay(200);
                if (!_rooms.ContainsKey(roomCode)) return;
                room = _rooms[roomCode];
            }

            // Enviar cuenta regresiva: 5,4,3,2,1
            for (int i = 5; i >= 0; i--)
            {
                if (i == 0)
                {
                    // Iniciar ronda
                    await StartRound(roomCode);
                    return;
                }
                await Clients.Group(roomCode).SendAsync("CountdownTick", i);
                await Task.Delay(1000);
                if (!_rooms.ContainsKey(roomCode)) return;
                room = _rooms[roomCode];
                // Si la ronda ya se activó por otro motivo, salir
                if (room.RoundActive) return;
            }
        }
        finally
        {
            if (_rooms.TryGetValue(roomCode, out var r)) r.CountdownActive = false;
        }
    }

    private async Task StartRound(string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room)) return;
        if (room.RoundActive) return;

        // Elegir palabra según longitud
        var word = GetWordByLength(room.WordLength);
        room.CurrentWord = word.ToUpper();
        room.RoundActive = true;

        // Reiniciar estados de jugadores vivos
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

        await Clients.Group(roomCode).SendAsync("RoundStarted", room.WordLength);
    }

    public async Task MakeGuess(string roomCode, string guess)
    {
        if (!_rooms.TryGetValue(roomCode, out var room) || !room.RoundActive)
        {
            await Clients.Caller.SendAsync("Error", "No hay ronda activa");
            return;
        }
        if (!room.Players.TryGetValue(Context.ConnectionId, out var player) || player.Status != "alive")
        {
            await Clients.Caller.SendAsync("Error", "Ya fuiste eliminado");
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
            await Clients.Group(roomCode).SendAsync("PlayerEliminated", player.Name);
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

        await Clients.Group(roomCode).SendAsync("PlayersUpdate", GetPlayersList(room));

        // Si todos los vivos adivinaron, terminar ronda
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
            await Clients.Group(roomCode).SendAsync("PlayerEliminated", p.Name);
        }

        if (survivors.Count == 1)
        {
            await Clients.Group(roomCode).SendAsync("GameEnded", survivors.First().Name);
            _rooms.TryRemove(roomCode, out _);
        }
        else if (survivors.Count == 0)
        {
            await Clients.Group(roomCode).SendAsync("GameEnded", null);
            _rooms.TryRemove(roomCode, out _);
        }
        else
        {
            await Clients.Group(roomCode).SendAsync("RoundEnded", survivors.Select(s => s.Name).ToList(), room.CurrentWord);
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

// ==================== CLASES AUXILIARES ====================
public class PlayerInRoom
{
    public string ConnectionId { get; set; }
    public string Name { get; set; }
    public int AttemptsLeft { get; set; } = 6;
    public string Status { get; set; } = "alive";
    public bool HasGuessedInRound { get; set; } = false;
    public List<AttemptInfo> Attempts { get; set; } = new();
}

public class Room
{
    public string Code { get; set; }
    public string CurrentWord { get; set; }
    public int WordLength { get; set; }
    public string Difficulty { get; set; } = "normal";
    public bool RoundActive { get; set; }
    public bool CountdownActive { get; set; }
    public ConcurrentDictionary<string, PlayerInRoom> Players { get; set; } = new();
}

public class AttemptInfo
{
    public string Guess { get; set; }
    public List<LetterResult> Result { get; set; }
}

public class LetterResult
{
    public char Letter { get; set; }
    public string Status { get; set; }
}
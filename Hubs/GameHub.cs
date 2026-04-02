using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace WordGuessAPI.Hubs;

public class GameHub : Hub
{
    private static readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly IHubContext<GameHub> _hubContext;

    public GameHub(IHubContext<GameHub> hubContext)
    {
        _hubContext = hubContext;
    }

    private static string NormalizeRoomCode(string code)
        => code.Trim().ToUpperInvariant();

    public async Task CreateRoom(string roomCode, string difficulty, string playerName)
    {
        roomCode = NormalizeRoomCode(roomCode);

        Console.WriteLine($"CreateRoom: {roomCode}, difficulty: {difficulty}, player: {playerName}");

        if (_rooms.ContainsKey(roomCode))
        {
            await Clients.Caller.SendAsync("Error", "La sala ya existe");
            return;
        }

        var wordLength = difficulty switch
        {
            "easy" => 4,
            "hard" => 6,
            _ => 5
        };

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
        roomCode = NormalizeRoomCode(roomCode);

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

        room.Players[Context.ConnectionId] = player;

        await Groups.AddToGroupAsync(Context.ConnectionId, roomCode);

        // Debug útil para validar que sí quedó en el grupo
        await Clients.Caller.SendAsync("JoinDebug", roomCode, Context.ConnectionId);
        await _hubContext.Clients.Group(roomCode).SendAsync("GroupProbe", roomCode);

        await _hubContext.Clients.Group(roomCode)
            .SendAsync("PlayersUpdate", GetPlayersList(room));

        await _hubContext.Clients.Group(roomCode)
            .SendAsync("WaitingForPlayers", room.Players.Count);

        Console.WriteLine($"Players in room {roomCode}: {room.Players.Count}");

        if (room.Players.Count >= 2 && !room.RoundActive && !room.CountdownActive)
        {
            room.CountdownActive = true;
            _ = StartCountdown(roomCode);
        }
    }

    public async Task LeaveRoom(string roomCode)
    {
        roomCode = NormalizeRoomCode(roomCode);

        if (_rooms.TryGetValue(roomCode, out var room))
        {
            room.Players.TryRemove(Context.ConnectionId, out _);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomCode);

            await _hubContext.Clients.Group(roomCode)
                .SendAsync("PlayersUpdate", GetPlayersList(room));

            if (room.Players.IsEmpty)
                _rooms.TryRemove(roomCode, out _);
            else
                await _hubContext.Clients.Group(roomCode)
                    .SendAsync("WaitingForPlayers", room.Players.Count);
        }
    }

    private async Task StartCountdown(string roomCode)
    {
        Console.WriteLine($"StartCountdown called for {roomCode}");

        if (!_rooms.TryGetValue(roomCode, out var room)) return;
        if (room.CountdownActive || room.RoundActive) return;

        room.CountdownActive = true;

        try
        {
            int waitCycles = 0;

            while (room.Players.Count < 2 && waitCycles < 50)
            {
                await Task.Delay(100);
                waitCycles++;

                if (!_rooms.TryGetValue(roomCode, out room))
                    return;
            }

            if (room.Players.Count < 2)
            {
                Console.WriteLine("No hay suficientes jugadores, cancelando countdown");
                return;
            }

            for (int i = 5; i >= 1; i--)
            {
                if (!_rooms.ContainsKey(roomCode)) return;

                Console.WriteLine($"Enviando tick: {i}");
                await _hubContext.Clients.Group(roomCode).SendAsync("CountdownTick", i);
                await Task.Delay(1000);

                if (!_rooms.TryGetValue(roomCode, out room))
                    return;

                if (room.RoundActive) return;
            }

            Console.WriteLine("Iniciando ronda...");
            await StartRound(roomCode);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error en countdown: {ex.Message}");
        }
        finally
        {
            if (_rooms.TryGetValue(roomCode, out var r))
                r.CountdownActive = false;
        }
    }

    private async Task StartRound(string roomCode)
    {
        Console.WriteLine($"StartRound for {roomCode}");

        if (!_rooms.TryGetValue(roomCode, out var room)) return;
        if (room.RoundActive) return;

        var word = GetWordByLength(room.WordLength);
        room.CurrentWord = word.ToUpperInvariant();
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

        await _hubContext.Clients.Group(roomCode)
            .SendAsync("RoundStarted", room.WordLength);

        Console.WriteLine($"Ronda iniciada con palabra: {room.CurrentWord}");
    }

    public async Task MakeGuess(string roomCode, string guess, string playerName)
    {
        roomCode = NormalizeRoomCode(roomCode);
        guess = guess.Trim().ToUpperInvariant();

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

        var isCorrect = guess == room.CurrentWord;

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

        await _hubContext.Clients.Group(roomCode)
            .SendAsync("PlayersUpdate", GetPlayersList(room));

        var alivePlayers = room.Players.Values.Where(p => p.Status == "alive").ToList();
        var allAliveGuessed = alivePlayers.All(p => p.HasGuessedInRound);

        if (allAliveGuessed && alivePlayers.Count > 0)
        {
            await EndRound(roomCode);
        }
    }

    private async Task EndRound(string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room)) return;
        if (!room.RoundActive) return;

        room.RoundActive = false;

        var survivors = room.Players.Values
            .Where(p => p.Status == "alive" && p.HasGuessedInRound)
            .ToList();

        var eliminated = room.Players.Values
            .Where(p => p.Status == "alive" && !p.HasGuessedInRound)
            .ToList();

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
            await _hubContext.Clients.Group(roomCode)
                .SendAsync("RoundEnded", survivors.Select(s => s.Name).ToList(), room.CurrentWord);

            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                if (_rooms.ContainsKey(roomCode))
                    await StartCountdown(roomCode);
            });
        }
    }

    private List<LetterResult> EvaluateGuess(string guess, string target)
    {
        guess = guess.ToUpperInvariant();
        target = target.ToUpperInvariant();

        var result = new List<LetterResult>();
        var targetCount = target.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());

        for (int i = 0; i < guess.Length; i++)
        {
            if (guess[i] == target[i])
            {
                result.Add(new LetterResult { Letter = guess[i], Status = "correct" });
                targetCount[guess[i]]--;
            }
            else
            {
                result.Add(null);
            }
        }

        for (int i = 0; i < guess.Length; i++)
        {
            if (result[i] != null) continue;

            var c = guess[i];

            if (targetCount.ContainsKey(c) && targetCount[c] > 0)
            {
                result[i] = new LetterResult { Letter = c, Status = "present" };
                targetCount[c]--;
            }
            else
            {
                result[i] = new LetterResult { Letter = c, Status = "absent" };
            }
        }

        return result;
    }

    private string GetWordByLength(int length)
    {
        var words = new Dictionary<int, List<string>>
        {
            { 4, new List<string> { "CASA", "GATO", "LUNA", "RICO" } },
            { 5, new List<string> { "MUNDO", "RATON", "SILLA", "PERRO", "MESA" } },
            { 6, new List<string> { "PROBAR", "RONDAS", "TRENES", "CORONA" } }
        };

        var list = words.ContainsKey(length) ? words[length] : words[5];
        return list[new Random().Next(list.Count)];
    }

    private object GetPlayersList(Room room)
    {
        return room.Players.Values.Select(p => new
        {
            name = p.Name,
            attemptsLeft = p.AttemptsLeft,
            status = p.Status,
            hasGuessed = p.HasGuessedInRound
        });
    }
}
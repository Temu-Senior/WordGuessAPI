using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace WordGuessAPI.Hubs;

public class GameHub : Hub
{
    private static ConcurrentDictionary<string, Room> _rooms = new();
    private readonly IHubContext<GameHub> _hubContext;

    public GameHub(IHubContext<GameHub> hubContext)
    {
        _hubContext = hubContext;
    }

    // 🔥 NORMALIZADOR (CLAVE)
    private string NormalizeRoomCode(string code)
    {
        return code.Trim().ToUpperInvariant();
    }

    public async Task CreateRoom(string roomCode, string difficulty, string playerName)
    {
        roomCode = NormalizeRoomCode(roomCode);

        Console.WriteLine($"CreateRoom: {roomCode}");

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
            WordLength = wordLength
        };

        _rooms.TryAdd(roomCode, room);

        await JoinRoom(roomCode, playerName);
    }

    public async Task JoinRoom(string roomCode, string playerName)
    {
        roomCode = NormalizeRoomCode(roomCode);

        Console.WriteLine($"JoinRoom: {roomCode}, conn: {Context.ConnectionId}");

        if (!_rooms.TryGetValue(roomCode, out var room))
        {
            await Clients.Caller.SendAsync("Error", "La sala no existe");
            return;
        }

        var player = new PlayerInRoom
        {
            ConnectionId = Context.ConnectionId,
            Name = playerName
        };

        room.Players[Context.ConnectionId] = player;

        await Groups.AddToGroupAsync(Context.ConnectionId, roomCode);

        // 🔥 DEBUG (puedes quitar luego)
        await Clients.Caller.SendAsync("JoinDebug", roomCode, Context.ConnectionId);
        await _hubContext.Clients.Group(roomCode).SendAsync("GroupProbe", roomCode);

        await _hubContext.Clients.Group(roomCode)
            .SendAsync("PlayersUpdate", GetPlayersList(room));

        await _hubContext.Clients.Group(roomCode)
            .SendAsync("WaitingForPlayers", room.Players.Count);

        Console.WriteLine($"Players in room {roomCode}: {room.Players.Count}");

        // 🔥 PREVENIR MULTI COUNTDOWN
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
        Console.WriteLine($"StartCountdown: {roomCode}");

        if (!_rooms.TryGetValue(roomCode, out var room)) return;
        if (room.RoundActive) return;

        try
        {
            for (int i = 5; i > 0; i--)
            {
                Console.WriteLine($"Tick {i} -> {roomCode}");

                await _hubContext.Clients.Group(roomCode)
                    .SendAsync("CountdownTick", i);

                await Task.Delay(1000);

                if (!_rooms.ContainsKey(roomCode)) return;
                room = _rooms[roomCode];

                if (room.RoundActive) return;
            }

            await StartRound(roomCode);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error countdown: {ex.Message}");
        }
        finally
        {
            if (_rooms.TryGetValue(roomCode, out var r))
                r.CountdownActive = false;
        }
    }

    private async Task StartRound(string roomCode)
    {
        Console.WriteLine($"StartRound: {roomCode}");

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

        await _hubContext.Clients.Group(roomCode)
            .SendAsync("RoundStarted", room.WordLength);

        Console.WriteLine($"Word: {room.CurrentWord}");
    }

    public async Task MakeGuess(string roomCode, string guess, string playerName)
    {
        roomCode = NormalizeRoomCode(roomCode);

        if (!_rooms.TryGetValue(roomCode, out var room) || !room.RoundActive)
        {
            await Clients.Caller.SendAsync("Error", "No hay ronda activa");
            return;
        }

        var player = room.Players.Values.FirstOrDefault(p => p.Name == playerName);

        if (player == null || player.Status != "alive")
        {
            await Clients.Caller.SendAsync("Error", "No válido");
            return;
        }

        var result = EvaluateGuess(guess, room.CurrentWord);

        player.Attempts.Add(new AttemptInfo { Guess = guess, Result = result });
        player.AttemptsLeft--;

        var isCorrect = guess.ToUpper() == room.CurrentWord;

        if (isCorrect)
            player.HasGuessedInRound = true;
        else if (player.AttemptsLeft <= 0)
        {
            player.Status = "eliminated";
            await _hubContext.Clients.Group(roomCode)
                .SendAsync("PlayerEliminated", player.Name);
        }

        await Clients.Caller.SendAsync("GuessResult", new
        {
            guess,
            resultArray = result,
            attemptsLeft = player.AttemptsLeft,
            won = isCorrect
        });

        await _hubContext.Clients.Group(roomCode)
            .SendAsync("PlayersUpdate", GetPlayersList(room));
    }

    // ===== Helpers =====

    private List<LetterResult> EvaluateGuess(string guess, string target)
    {
        guess = guess.ToUpper();
        target = target.ToUpper();

        var result = new List<LetterResult>();
        var targetCount = target.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());

        for (int i = 0; i < guess.Length; i++)
        {
            if (guess[i] == target[i])
            {
                result.Add(new LetterResult { Letter = guess[i], Status = "correct" });
                targetCount[guess[i]]--;
            }
            else result.Add(null);
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
                result[i] = new LetterResult { Letter = c, Status = "absent" };
        }

        return result;
    }

    private string GetWordByLength(int length)
    {
        var words = new Dictionary<int, List<string>>
        {
            { 4, new List<string> { "CASA", "GATO", "LUNA", "RICO" } },
            { 5, new List<string> { "MUNDO", "RATON", "SILLA" } },
            { 6, new List<string> { "PROBAR", "RONDAS" } }
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
            p.Status
        });
    }
}
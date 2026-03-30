private static ConcurrentDictionary<string, Room> _rooms = new();

public async Task CreateRoom(string roomCode)
{
    var room = new Room { Code = roomCode, RoundActive = false };
    _rooms.TryAdd(roomCode, room);
    await JoinRoom(roomCode);
}

public async Task JoinRoom(string roomCode)
{
    if (!_rooms.TryGetValue(roomCode, out var room))
    {
        await Clients.Caller.SendAsync("Error", "Sala no existe");
        return;
    }
    var user = Context.UserIdentifier;
    var username = Context.User?.Identity?.Name ?? user;
    var player = new PlayerInRoom { ConnectionId = Context.ConnectionId, Name = username };
    room.Players.TryAdd(Context.ConnectionId, player);
    await Groups.AddToGroupAsync(Context.ConnectionId, roomCode);
    await Clients.Group(roomCode).SendAsync("PlayersUpdate", GetPlayersList(room));
    // Si es el primer jugador y la ronda no está activa, puede iniciar cuenta regresiva (opcional)
    if (room.Players.Count == 1 && !room.RoundActive)
    {
        _ = Task.Run(() => StartRoundCountdown(roomCode));
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
    }
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
    // Validar longitud
    if (guess.Length != room.WordLength)
    {
        await Clients.Caller.SendAsync("Error", $"La palabra debe tener {room.WordLength} letras");
        return;
    }
    // Comparar con room.CurrentWord
    var result = EvaluateGuess(guess, room.CurrentWord);
    player.Attempts.Add(new AttemptInfo { Guess = guess, Result = result });
    player.AttemptsLeft--;
    var won = guess.ToUpper() == room.CurrentWord;
    var gameCompleted = false;
    if (won)
    {
        gameCompleted = true;
        player.Status = "winner";
        room.Winner = player.Name;
        room.RoundActive = false;
        await Clients.Group(roomCode).SendAsync("RoundEnded", player.Name, room.CurrentWord);
        // Opcional: guardar en BD la victoria
    }
    else if (player.AttemptsLeft <= 0)
    {
        player.Status = "eliminated";
        await Clients.Group(roomCode).SendAsync("PlayerEliminated", player.Name);
        // Verificar si todos murieron o alguien ya ganó
        if (!room.Players.Values.Any(p => p.Status == "alive"))
        {
            room.RoundActive = false;
            await Clients.Group(roomCode).SendAsync("RoundEnded", null, room.CurrentWord);
        }
        gameCompleted = true; // para el jugador actual
    }
    // Enviar resultado al jugador
    await Clients.Caller.SendAsync("GuessResult", new
    {
        success = true,
        guess,
        resultArray = result,
        attemptsLeft = player.AttemptsLeft,
        gameCompleted,
        won,
        word = gameCompleted ? room.CurrentWord : null
    });
    // Actualizar lista de jugadores para todos
    await Clients.Group(roomCode).SendAsync("PlayersUpdate", GetPlayersList(room));
    if (won)
    {
        // Detener temporizador de ronda
        room.RoundTimer?.Dispose();
    }
}

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
        else
        {
            result.Add(null);
        }
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
        else
        {
            result[i] = new LetterResult { Letter = c, Status = "absent" };
        }
    }
    return result;
}

private async Task StartRoundCountdown(string roomCode)
{
    if (!_rooms.TryGetValue(roomCode, out var room)) return;
    // Esperar a que haya al menos 2 jugadores o un tiempo? Por simplicidad, esperar 5 segundos después de que el primer jugador se une
    await Task.Delay(5000);
    if (!_rooms.ContainsKey(roomCode)) return;
    room = _rooms[roomCode];
    if (room.RoundActive) return;
    await Clients.Group(roomCode).SendAsync("RoundStarting", 5);
    await Task.Delay(5000);
    if (!_rooms.ContainsKey(roomCode)) return;
    // Elegir palabra según dificultad (puedes leerla de la BD o usar una lista)
    // Por ahora, usamos palabras predefinidas según longitud
    var difficulty = "normal"; // podrías obtenerla de la sala
    var word = GetWordByLength(room.WordLength);
    room.CurrentWord = word.ToUpper();
    room.RoundActive = true;
    room.RoundStartTime = DateTime.UtcNow;
    room.Winner = null;
    // Reiniciar estados de jugadores
    foreach (var p in room.Players.Values)
    {
        p.AttemptsLeft = 6;
        p.Status = "alive";
        p.Attempts.Clear();
    }
    await Clients.Group(roomCode).SendAsync("RoundStarted", room.WordLength, room.TimeLimitSeconds);
    // Iniciar temporizador de ronda
    room.RoundTimer = new Timer(async _ =>
    {
        if (room.RoundActive)
        {
            room.RoundActive = false;
            await Clients.Group(roomCode).SendAsync("RoundEnded", null, room.CurrentWord);
        }
    }, null, room.TimeLimitSeconds * 1000, Timeout.Infinite);
}

private string GetWordByLength(int length)
{
    // Aquí consultas tu base de datos para obtener una palabra aleatoria de esa longitud
    // Por ahora, palabras de ejemplo
    var words = new Dictionary<int, List<string>>
    {
        { 4, new List<string> { "CASA", "GATO", "LUNA", "SOL" } },
        { 5, new List<string> { "MUNDO", "RATON", "SILLA" } },
        { 6, new List<string> { "PROGRAMAR", "SERVIDOR", "BASE" } } // Nota: algunas no tienen 6, ajusta
    };
    var list = words.ContainsKey(length) ? words[length] : words[5];
    var random = new Random();
    return list[random.Next(list.Count)];
}

private object GetPlayersList(Room room)
{
    return room.Players.Values.Select(p => new { p.Name, p.AttemptsLeft, p.Status });
}

// Método para que el admin fuerce nueva ronda
public async Task ForceNewRound(string roomCode)
{
    var user = Context.UserIdentifier;
    var isAdmin = Context.User?.IsInRole("admin") ?? false;
    if (!isAdmin) return;
    if (_rooms.TryGetValue(roomCode, out var room))
    {
        if (room.RoundTimer != null) await room.RoundTimer.DisposeAsync();
        await StartRoundCountdown(roomCode);
    }
}
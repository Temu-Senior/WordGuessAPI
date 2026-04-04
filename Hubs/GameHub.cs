using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace WordGuessAPI.Hubs;

// ==================== CLASES AUXILIARES ====================
public class PlayerInRoom
{
    public string ConnectionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int AttemptsLeft { get; set; } = 6;
    public string Status { get; set; } = "alive";
    public bool HasGuessedInRound { get; set; } = false;
    public List<AttemptInfo> Attempts { get; set; } = new();
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}

public class Room
{
    public string Code { get; set; } = string.Empty;
    public string CurrentWord { get; set; } = string.Empty;
    public int WordLength { get; set; }
    public string Difficulty { get; set; } = "normal";
    public bool RoundActive { get; set; }
    public bool CountdownActive { get; set; }
    public bool GameStarted { get; set; } = false;
    public string OwnerConnectionId { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public ConcurrentDictionary<string, PlayerInRoom> Players { get; set; } = new();
    public HashSet<string> UsedWords { get; set; } = new();
}

public class AttemptInfo
{
    public string Guess { get; set; } = string.Empty;
    public List<LetterResult> Result { get; set; } = new();
}

public class LetterResult
{
    public char Letter { get; set; }
    public string Status { get; set; } = string.Empty; // "correct", "present", "absent"
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

    // ==================== SALAS ====================
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
            CountdownActive = false,
            GameStarted = false,
            OwnerConnectionId = Context.ConnectionId,
            OwnerName = playerName
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
        if (room.Players.Count >= 100)
        {
            await Clients.Caller.SendAsync("Error", "La sala está llena (máximo 100 jugadores)");
            return;
        }
        var player = new PlayerInRoom
        {
            ConnectionId = Context.ConnectionId,
            Name = playerName,
            Status = "alive",
            JoinedAt = DateTime.UtcNow
        };
        room.Players.TryAdd(Context.ConnectionId, player);
        await Groups.AddToGroupAsync(Context.ConnectionId, roomCode);

        await _hubContext.Clients.Group(roomCode).SendAsync("PlayersUpdate", GetPlayersList(room));
        await _hubContext.Clients.Group(roomCode).SendAsync("WaitingForPlayers", room.Players.Count);
        await _hubContext.Clients.Group(roomCode).SendAsync("RoomOwnerChanged", room.OwnerName);

        Console.WriteLine($"Players in room {roomCode}: {room.Players.Count}");
    }

    public async Task LeaveRoom(string roomCode)
    {
        if (_rooms.TryGetValue(roomCode, out var room))
        {
            var isOwner = (Context.ConnectionId == room.OwnerConnectionId);
            room.Players.TryRemove(Context.ConnectionId, out _);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomCode);

            if (isOwner && !room.Players.IsEmpty)
            {
                var newOwner = room.Players.Values.OrderBy(p => p.JoinedAt).First();
                room.OwnerConnectionId = newOwner.ConnectionId;
                room.OwnerName = newOwner.Name;
                await _hubContext.Clients.Group(roomCode).SendAsync("RoomOwnerChanged", room.OwnerName);
            }

            await _hubContext.Clients.Group(roomCode).SendAsync("PlayersUpdate", GetPlayersList(room));
            if (room.Players.IsEmpty)
                _rooms.TryRemove(roomCode, out _);
            else
                await _hubContext.Clients.Group(roomCode).SendAsync("WaitingForPlayers", room.Players.Count);
        }
    }

    // ==================== INICIO DE JUEGO (SOLO ANFITRIÓN) ====================
    public async Task StartGameByHost(string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
        {
            await Clients.Caller.SendAsync("Error", "La sala no existe");
            return;
        }
        if (Context.ConnectionId != room.OwnerConnectionId)
        {
            await Clients.Caller.SendAsync("Error", "Solo el anfitrión puede iniciar el juego");
            return;
        }
        if (room.GameStarted)
        {
            await Clients.Caller.SendAsync("Error", "El juego ya comenzó");
            return;
        }
        if (room.Players.Count < 2)
        {
            await Clients.Caller.SendAsync("Error", "Se necesitan al menos 2 jugadores para comenzar");
            return;
        }
        room.GameStarted = true;
        _ = Task.Run(() => StartCountdown(roomCode));
    }

    // ==================== COUNTDOWN Y RONDAS ====================
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

        var word = GetNewWord(room.WordLength, room.UsedWords);
        if (string.IsNullOrEmpty(word))
        {
            room.UsedWords.Clear();
            word = GetWordByLength(room.WordLength);
        }
        room.CurrentWord = word.ToUpper();
        room.UsedWords.Add(room.CurrentWord);
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

    // ==================== LÓGICA DE JUEGO ====================
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

        // Evaluar el intento (Wordle-style, maneja letras repetidas correctamente)
        var result = EvaluateGuess(guess, room.CurrentWord);
        var resultArray = result.Select(r => new { letter = r.Letter, status = r.Status }).ToList();

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

        string wordToSend = isCorrect ? room.CurrentWord : null;

        await Clients.Caller.SendAsync("GuessResult", new
        {
            success = true,
            guess,
            resultArray = resultArray,   // <-- clave para colorear teclado
            attemptsLeft = player.AttemptsLeft,
            gameCompleted = (isCorrect || player.Status == "eliminated"),
            won = isCorrect,
            word = wordToSend
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

        // Primera pasada: marcar letras correctas
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

        // Segunda pasada: marcar letras presentes (amarillo) o ausentes (gris)
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

    private string GetNewWord(int length, HashSet<string> usedWords)
    {
        var allWords = GetAllWordsByLength(length);
        var available = allWords.Except(usedWords).ToList();
        if (available.Count == 0) return null;
        return available[new Random().Next(available.Count)];
    }

    private List<string> GetAllWordsByLength(int length)
    {
        // Asegúrate de que estas listas contengan SOLO palabras de 4, 5 y 6 letras.
        var palabras4 = new List<string> { "CASA", "GATO", "LUNA", "RICO", "MESA", "PISO", "MANO", "SALA", "COPA", "BOCA", "RATA", "PATO", "VACA", "LOMA", "PALA", "MOTO", "FOCA", "BOTA", "COLA", "LATA", "MULA", "NIDO", "PICO", "SAPO", "TAPA", "UNAS", "VINO", "ROSA", "FLOR", "AZUL", "VERDE", "NIEVE", "FUEGO", "HIELO", "JEFE", "KILO", "LADO", "MADRE", "NARIZ", "OJO", "PADRE", "QUESO", "RIOS", "SALUD", "TIGRE", "ARBOL", "CABLE", "DEDO", "PERA", "MELON", "UVAS", "PAN", "ARROZ", "SOPA", "CALDO", "CARNE", "AVE", "BUEY", "CERDO", "POLLO", "HUEVO", "LECHE", "CREMA" };
        var palabras5 = new List<string> { "MUNDO", "RATON", "SILLA", "PERRO", "MESA", "PLUMA", "CARRO", "FLOR", "MANO", "CASA", "GATO", "LUNA", "SOL", "RICO", "PISO", "SALA", "COPA", "BOCA", "RATA", "PATO", "VACA", "LOMA", "PALA", "MOTO", "FOCA", "BOTA", "COLA", "LATA", "MULA", "NIDO", "PICO", "SAPO", "TAPA", "UÑA", "VINO", "ARBOL", "CABLE", "DEDO", "FUEGO", "HIELO", "JEFE", "KILO", "LADO", "MADRE", "NARIZ", "OJO", "PADRE", "QUESO", "RIO", "SALUD", "TIGRE", "ROBLE", "NUBE", "LLUVIA", "CONEJO", "CABALLO", "OVEJA", "CERDO", "CABRA", "BURRO", "LEON", "JAGUAR", "PANTER", "GUEPAR", "LOBO", "ZORRO", "OSO", "CIERVO", "ALCE", "JIRAFA", "ELEFANTE", "RINOCER", "HIPOPO", "CANGREJO", "LANGOSTA", "CAMARON", "PULPO", "CALAMAR", "MEDUSA", "ESTRELLA", "ERIZO", "CORAL", "BALLENA", "DELFIN", "TIBURON", "RAYA", "MANTARRAYA", "SALMON", "TRUCHA", "ATUN", "BACALAO", "MERLUZA", "LENGUADO", "RODABALLO", "DORADA", "BREMA", "CARPA", "PEZ", "RANA", "TRITON", "SALAMANDRA", "LAGARTIJA", "GECKO", "IGUANA", "CAMALEON", "SERPIENTE", "VIBORA", "COBRA", "BOA", "PITON", "ANACONDA", "COCODRILO", "CAIMAN", "ALIGATOR", "TORTUGA", "GALAPAGO", "AVE", "PAJARO", "GORRION", "CANARIO", "PERICO", "LORO", "GUACAMAYA", "TUCAN", "COLIBRI", "AGUILA", "HALCON", "BUITRE", "CONDOR", "FLAMENCO", "PELICANO", "CIGÜEÑA", "GRULLA", "GARZA", "PATIO", "PATO", "GANSO", "CISNE", "PAVO", "FAISAN", "PERDIZ", "CODORNIZ", "PALOMA", "TORTOLA", "CUERVO", "URRACA", "GRAJO", "CHOVA", "MIRLO", "ZORZAL", "RUISENOR", "CALANDRIA", "ALONDRA" };
        var palabras6 = new List<string> { "PROBAR", "SERVID", "CLIENT", "RONDAS", "MUNDOS", "RATONES", "SILLAS", "PERROS", "MESAS", "PLUMAS", "CARROS", "FLORES", "MANOS", "CASAS", "GATOS", "LUNAS", "SOLES", "RICOS", "PISOS", "SALAS", "COPAS", "BOCAS", "RATAS", "PATOS", "VACAS", "LOMAS", "PALAS", "MOTOS", "FOCAS", "BOTAS", "COLAS", "LATAS", "MULAS", "NIDOS", "PICOS", "SAPOS", "TAPAS", "UNAS", "VINOS", "ARBOLES", "CABLES", "DEDOS", "FUEGOS", "HIELOS", "JEFES", "KILOS", "LADOS", "MADRES", "NARICES", "OJOS", "PADRES", "QUESOS", "RIOS", "SALUDOS", "TIGRES", "ROBLES", "NUBES", "LLUVIAS", "CABALLO", "CONEJO", "CORDER", "CABRAS", "BURROS", "LEONES", "JAGUAR", "PANTER", "GUEPAR", "LOBOS", "ZORROS", "OSOS", "CIERVOS", "ALCES", "JIRAFAS", "ELEFANT", "RINOCER", "HIPOPOT", "CANGREJ", "LANGOST", "CAMARON", "PULPOS", "CALAMAR", "MEDUSAS", "ESTRELL", "ERIZOS", "CORALES", "BALLENA", "DELFINES", "TIBURON", "RAYAS", "MANTAS", "SALMON", "TRUCHAS", "ATUNES", "BACALAO", "MERLUZA", "LENGUAD", "RODABAL", "DORADAS", "BREMAS", "CARDUMEN", "RANAS", "TRITONES", "SALAMAN", "LAGARTI", "GECKOS", "IGUANAS", "CAMALEON", "SERPIEN", "VIBORAS", "COBRAS", "BOAS", "PITONES", "ANACOND", "COCODRIL", "CAIMANES", "ALIGATO", "TORTUGAS", "GALAPAG", "AVES", "PAJAROS", "GORRION", "CANARIO", "PERICOS", "LOROS", "GUACAMA", "TUCANES", "COLIBRI", "AGUILAS", "HALCONES", "BUITRES", "CONDORES", "FLAMENC", "PELICAN", "CIGÜEÑA", "GRULLAS", "GARZAS", "PATIOS", "GANSOS", "CISNES", "PAVOS", "FAISANES", "PERDICES", "CODORNIZ", "PALOMAS", "TORTOLAS", "CUERVOS", "URRACAS", "GRAJOS", "CHOVAS", "MIRLOS", "ZORZALES", "RUISENOR", "CALANDRI", "ALONDRAS" };

        return length switch
        {
            4 => palabras4,
            5 => palabras5,
            6 => palabras6,
            _ => new List<string>()
        };
    }

    private string GetWordByLength(int length)
    {
        var all = GetAllWordsByLength(length);
        return all.Count > 0 ? all[new Random().Next(all.Count)] : length == 4 ? "CASA" : length == 5 ? "MUNDO" : "PROBAR";
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
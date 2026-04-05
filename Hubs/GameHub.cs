using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Security.Claims;

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
[Authorize]
public class GameHub : Hub
{
    private static ConcurrentDictionary<string, Room> _rooms = new();
    private readonly IHubContext<GameHub> _hubContext;

    public GameHub(IHubContext<GameHub> hubContext)
    {
        _hubContext = hubContext;
    }

    private string GetPlayerName() => Context.User?.Identity?.Name ?? "Anónimo";

    // ==================== SALAS ====================
    public async Task CreateRoom(string roomCode, string difficulty)
    {
        var playerName = GetPlayerName();
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
        await JoinRoom(roomCode);
    }

    public async Task JoinRoom(string roomCode)
    {
        var playerName = GetPlayerName();
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

        // Buscar si ya existe un jugador con el mismo nombre en la sala
        var existingPlayer = room.Players.Values.FirstOrDefault(p => p.Name == playerName);
        if (existingPlayer != null)
        {
            // Eliminar la entrada antigua
            room.Players.TryRemove(existingPlayer.ConnectionId, out _);
            Console.WriteLine($"Jugador {playerName} reconectado, se reemplaza conexión antigua.");
        }

        // Crear el nuevo jugador (si existía, se copia el estado)
        var player = new PlayerInRoom
        {
            ConnectionId = Context.ConnectionId,
            Name = playerName,
            Status = existingPlayer?.Status ?? "alive",
            AttemptsLeft = existingPlayer?.AttemptsLeft ?? 6,
            HasGuessedInRound = existingPlayer?.HasGuessedInRound ?? false,
            Attempts = existingPlayer?.Attempts ?? new List<AttemptInfo>(),
            JoinedAt = DateTime.UtcNow
        };
        room.Players.TryAdd(Context.ConnectionId, player);

        // Si el jugador que reconecta es el anfitrión, actualizar OwnerConnectionId
        if (room.OwnerName == playerName)
        {
            room.OwnerConnectionId = Context.ConnectionId;
            await _hubContext.Clients.Group(roomCode).SendAsync("RoomOwnerChanged", room.OwnerName);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, roomCode);
        await _hubContext.Clients.Group(roomCode).SendAsync("PlayersUpdate", GetPlayersList(room));
        await _hubContext.Clients.Group(roomCode).SendAsync("WaitingForPlayers", room.Players.Count);

        // Si la ronda ya está activa, enviar el estado actual al jugador que reconecta
        if (room.RoundActive)
        {
            // Enviar el evento RoundStarted con la longitud
            await Clients.Caller.SendAsync("RoundStarted", room.WordLength);
            // Enviar los intentos anteriores del jugador (para reconstruir el tablero)
            if (player.Attempts.Any())
            {
                foreach (var attempt in player.Attempts)
                {
                    var resultArray = attempt.Result.Select(r => new { letter = r.Letter, status = r.Status }).ToList();
                    await Clients.Caller.SendAsync("GuessResult", new
                    {
                        success = true,
                        guess = attempt.Guess,
                        resultArray = resultArray,
                        attemptsLeft = player.AttemptsLeft,
                        gameCompleted = false,
                        won = false,
                        word = (string?)null
                    });
                }
            }
        }

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

    // ==================== INICIO DE JUEGO ====================
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
        Console.WriteLine($"Round started with word length {room.WordLength}");
    }

    // ==================== LÓGICA DE JUEGO ====================
    public async Task MakeGuess(string roomCode, string guess)
    {
        var playerName = GetPlayerName();
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
            resultArray = resultArray,
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

    private string GetNewWord(int length, HashSet<string> usedWords)
    {
        var allWords = GetAllWordsByLength(length);
        var available = allWords.Except(usedWords).ToList();
        if (available.Count == 0) return null;
        return available[new Random().Next(available.Count)];
    }

    private List<string> GetAllWordsByLength(int length)
    {
        // ==================== PALABRAS DE 4 LETRAS ====================
        var palabras4 = new List<string>
        {
            "ABRA", "ABRE", "ACTO", "ALMA", "AMOR", "ARCO", "ARDE", "ARTE", "ASCO", "ASNO",
            "AUTO", "AYER", "BALA", "BEBE", "BOCA", "BOLA", "BONO", "BOTA", "BUZO", "CAFE",
            "CAJA", "CAMA", "CAPA", "CARA", "CASO", "CAZA", "CEBA", "CEPA", "CELO", "CERA",
            "CESA", "CIMA", "CINE", "COCO", "COGE", "COLA", "COME", "COPA", "CORO", "COSE",
            "CREA", "CUNA", "DADO", "DALE", "DATO", "DEBE", "DEDO", "DEJA", "DIGA", "DIGO",
            "DIOS", "DUDA", "DUNA", "DURA", "DURO", "ECHO", "EDAD", "ELLA", "ESTE", "FAMA",
            "FARO", "FIJA", "FIJO", "FILA", "FINA", "FINO", "FOCA", "FONO", "FOSA", "FOTO",
            "GANA", "GATO", "GEMA", "GIME", "GIRA", "GIRO", "GOTA", "GOZA", "GUIA", "HABA",
            "HACE", "HADA", "HALA", "HALO", "HIJA", "HIJO", "HILO", "HOLA", "HOYO", "HUMO",
            "HUYE", "IDEA", "IMAN", "ISLA", "JALA", "JOSE", "JUGO", "JURA", "LAGO", "LAMA",
            "LANA", "LATA", "LAVA", "LEAL", "LEMA", "LENA", "LEVE", "LIJA", "LIMA", "LINO",
            "LIRA", "LOBA", "LOCA", "LOCO", "LOGO", "LOMA", "LONA", "LORO", "LOSA", "LOTE",
            "LOZA", "LUJO", "LUNA", "MAGO", "MALO", "MANO", "MAPA", "MASA", "MAYO", "MAZA",
            "MAZO", "MECE", "MERA", "META", "MIRA", "MODO", "MOJA", "MOLA", "MOLE", "MONO",
            "MORA", "MOTO", "MUDA", "MUDO", "MULA", "MURO", "NACE", "NADA", "NATA", "NAVE",
            "NENA", "NIDO", "NIÑA", "NIÑO", "NODO", "NOTA", "NUBE", "NUCA", "OBOE", "OBRA",
            "OCIO", "ODIO", "OIGO", "OLLA", "ONDA", "ORAL", "ORBE", "ORLA", "OVAL", "PAGA",
            "PAJA", "PALO", "PAPA", "PARA", "PARO", "PASA", "PASO", "PATA", "PATO", "PAVO",
            "PECA", "PELA", "PENA", "PELO", "PERA", "PESA", "PESO", "PICA", "PICO", "PIDE",
            "PIEL", "PILA", "PINO", "PISA", "PISO", "PLAN", "PODA", "POLO", "POMA", "PONE",
            "PORO", "POSE", "POSA", "POTE", "POZO", "PUMA", "PUNA", "PURA", "PURO", "RABO",
            "RAMO", "RANA", "RARO", "RATO", "RAZA", "REMA", "REPO", "REZA", "RICO", "RIEL",
            "RIGE", "RIMA", "ROBA", "ROCA", "RODE", "ROLA", "ROMA", "ROPA", "ROSA", "ROTO",
            "RUDA", "RUDO", "RUGE", "RUTA", "SACO", "SAGA", "SALA", "SALE", "SANA", "SANO",
            "SAPO", "SECA", "SECO", "SEDA", "SEIS", "SERA", "SETA", "SIGA", "SILO", "SOBA",
            "SOGA", "SOJA", "SOLA", "SOLE", "SOLO", "SOMA", "SONA", "SOPA", "SUBE", "SUDA",
            "SUDO", "SUMA", "TACO", "TALA", "TAPA", "TAZA", "TELA", "TEMA", "TIPO", "TIRO",
            "TIZA", "TOCA", "TODO", "TOMA", "TONO", "TOPE", "TORO", "TRAE", "TREN", "TUBA",
            "TUBO", "TUNA", "UREA", "URNA", "VACA", "VALE", "VARA", "VASO", "VELA", "VENA",
            "VERA", "VIDA", "VINE", "VINO", "VIRA", "VISA", "VIVE", "VOTA", "YEMA", "YOGA",
            "YUCA", "ZAFA", "ZAFO", "ZAGA", "ZAPE", "ZONA", "ZUMO"
        };

        // ==================== PALABRAS DE 5 LETRAS ====================
        var palabras5 = new List<string>
        {
            "ABEJA", "ABRIR", "ABUSO", "ACABO", "ACERO", "ACOGE", "ACOSO", "ACTUA", "ACUDE", "AGUJA",
            "AHORC", "AHORA", "AIRE", "AJENO", "ALABO", "ALAMO", "ALBUM", "ALDEA", "ALEJA", "ALERO",
            "ALFIL", "ALGAR", "ALIJO", "ALISO", "ALOCA", "ALOJA", "ALOMO", "ALOSA", "ALTAR", "ALUDA",
            "AMABA", "AMADO", "AMAGO", "AMARA", "AMASA", "AMBON", "AMIGO", "AMPLO", "AMUCA", "ANCHO",
            "ANCLA", "ANGEL", "ANGLA", "ANIMA", "ANIMO", "ANOTA", "ANTES", "ANTRO", "ANULA", "APAGA",
            "APELA", "APOYA", "APURA", "ARBOL", "ARDOR", "ARENA", "ARGOT", "ARMAS", "ARPON", "ARRAS",
            "ARREA", "ARROZ", "ASADO", "ASOMA", "ASUME", "ATACA", "ATAJO", "ATAPA", "ATRAE", "ATROZ",
            "ATUNA", "AVARO", "AVENA", "AVION", "AVISA", "AVISO", "AYUDA", "AYUNO", "AZOTE", "AZUCAR",
            "BACAL", "BAHIA", "BAILE", "BAJIO", "BALDE", "BANCO", "BANDA", "BAÑOS", "BARBA", "BARCO",
            "BARRO", "BASAR", "BASTO", "BATAL", "BATIR", "BAUL", "BAYOU", "BELGA", "BELLO", "BESAR",
            "BINGO", "BISTE", "BIZCO", "BLOCA", "BLUSA", "BOLSA", "BOMBA", "BONITO", "BORDO", "BORLA",
            "BOSCO", "BOTAR", "BOTIN", "BOTON", "BOXEO", "BRAZO", "BREVE", "BRILLO", "BROMA", "BRUJA",
            "BUCHE", "BURLA", "BUZON", "CABAL", "CABRA", "CACTO", "CAIDA", "CAIRO", "CALMA", "CALOR",
            "CALVO", "CAMPO", "CANAL", "CANTO", "CAPAZ", "CAPUL", "CARGA", "CARPA", "CARRO", "CARTA",
            "CASCO", "CAUSA", "CAVAR", "CAZAR", "CEBRA", "CERCA", "CERDO", "CERRO", "CHICO", "CHILE",
            "CHIVO", "CHOCA", "CHUPO", "CICLO", "CIEGO", "CIELO", "CIENO", "CIFRA", "CINCO", "CINTA",
            "CIRCO", "CIRRO", "CISNE", "CITAR", "CLARO", "CLAVO", "CLIMA", "COBRA", "COBRE", "COCOA",
            "CODOS", "COGER", "COLON", "COLOR", "COMIC", "COMER", "COMO", "CONDE", "CONGA", "CORAL",
            "CORMO", "CORNE", "CORNO", "CORNU", "CORRE", "CORTA", "CORTE", "COSTA", "COSTU", "COTIZ",
            "CREMA", "CRUCE", "CRUEL", "CUADRO", "CUAJO", "CUEVA", "CULPA", "CUMPL", "CUNHA", "CUOTA",
            "CURIA", "CURVA", "DANZA", "DEBUT", "DECIR", "DEDAL", "DELTA", "DENSE", "DEPOT", "DEUDA",
            "DIANO", "DICTA", "DISCO", "DOBLE", "DOLOR", "DONDE", "DORSO", "DRAMA", "DUCHA", "DUELO",
            "DULCE", "DUNAR", "DUQUE", "EBANO", "ECHAR", "EDUCA", "EMITE", "EMPLO", "EMULA", "ENOJA",
            "ENSAYO", "ENTRA", "ENTRE", "ERIZO", "ERROR", "ESCALA", "ESCUD", "ESPIA", "ETAPA", "ETNIA",
            "EXAMN", "EXTRA", "FAENA", "FANGO", "FAVOR", "FERIA", "FEROZ", "FIBRA", "FIEBRE", "FINCA",
            "FIRME", "FLACO", "FLAUTA", "FLOJO", "FLORE", "FLUYE", "FOLIO", "FONDO", "FONJA", "FORMA",
            "FORTE", "FORUM", "FOSIL", "FRANC", "FRENO", "FRESA", "FRIAN", "FRIAL", "FRITO", "FRUTA",
            "FUEGO", "FUERZ", "FUNDA", "GALAN", "GALLO", "GAMBA", "GANSO", "GARZA", "GASTO", "GENIO",
            "GLOBO", "GLOSA", "GOLFO", "GOLPE", "GORDO", "GORRA", "GOZAN", "GRACE", "GRADO", "GRAMA",
            "GRASA", "GRAVE", "GRECA", "GRIPE", "GRISO", "GROTO", "GRUPO", "GUAPO", "GUARO", "GUION",
            "GUSTO", "HABLA", "HACER", "HAMPA", "HAREN", "HEDOR", "HELAD", "HIELO", "HINCO", "HIPOS",
            "HOGAR", "HONGO", "HONOR", "HORMA", "HUESO", "HUEVO", "HUMOR", "IDEAL", "IDIOM", "IGLUS",
            "IGUAL", "ILUSO", "IMPAR", "IMPON", "INDIE", "INDUL", "INFAM", "INFOR", "INGAS", "INTER",
            "IRONI", "ISLAM", "JABON", "JACAL", "JARRA", "JAULA", "JEANS", "JEMER", "JINST", "JOCUL",
            "JOUER", "JOYAS", "JUEGO", "JUEZA", "JUGON", "JUICI", "JUNTO", "JURAR", "KARMA", "KOALA",
            "LABIO", "LABOR", "LACAR", "LACOS", "LACRA", "LADRA", "LAICO", "LAPIZ", "LARGO", "LASCA",
            "LASER", "LATIR", "LAZON", "LECHE", "LECHO", "LEGAL", "LEGUA", "LEJOS", "LENTO", "LEONA",
            "LEPRA", "LETRA", "LIMON", "LINEA", "LISTO", "LLAMA", "LLANO", "LLEVA", "LLORA", "LLOVE",
            "LOCAL", "LOGRO", "LUCHA", "LUGAR", "LUMPO", "LUNAR", "LURCO", "LUSCO", "MACRO", "MADRE",
            "MAGIA", "MAGMA", "MALCO", "MAMBO", "MAMUT", "MANGA", "MANGO", "MANIA", "MANOS", "MANTA",
            "MAÑAN", "MAQUI", "MARCA", "MARCO", "MAREA", "MARFIL", "MARIO", "MARZO", "MAYOR", "MEDIA",
            "MEDIO", "MEJOR", "MELON", "MENOS", "MENTE", "MEÑIQ", "METRO", "MIEDO", "MIREN", "MISMA",
            "MISMO", "MITRA", "MIXTO", "MOLDE", "MONJE", "MONTE", "MORAL", "MORBO", "MORSA", "MOSCA",
            "MOTIN", "MOTOR", "MOUCO", "MUJER", "MUNDO", "MUÑEC", "MUSGO", "NACAR", "NADAR", "NARIZ",
            "NEGRO", "NERVI", "NEVAD", "NIETO", "NIÑEZ", "NIVEL", "NOCHE", "NOMBR", "NORMA", "NUDOS",
            "NUEVO", "OBESO", "OCASO", "ODIAR", "OESTE", "OLFAT", "OLIVO", "OMBLE", "ORDEN", "ORUGA",
            "OVEJA", "OVULO", "PADRE", "PAGAR", "PAJARO", "PALCO", "PALMA", "PALMO", "PAMPA", "PANDA",
            "PARDO", "PAREO", "PARGO", "PARQU", "PARRA", "PARTE", "PATIO", "PATON", "PECHO", "PEDAL",
            "PEDIR", "PEGAD", "PELMA", "PELON", "PENCO", "PERLA", "PERRO", "PESCA", "PETALO", "PIANO",
            "PICAR", "PIEZA", "PILAR", "PILON", "PINTA", "PIÑON", "PIRCA", "PIRON", "PITAR", "PIZZA",
            "PLAGA", "PLANO", "PLATA", "PLAZA", "PLENA", "PLUMA", "POBRE", "PODER", "POEMA", "POLVO",
            "PONER", "PONZO", "POPUL", "PORTA", "POTRO", "PREMI", "PRESA", "PRIMA", "PRIMO", "PRIOR",
            "PRISM", "PROBA", "PROEL", "PROSA", "PUBLI", "PUEDO", "PUERT", "PULGA", "PULPO", "PUNTO",
            "PUÑAL", "PUÑOS", "QUEJA", "QUEMA", "QUESO", "RADIO", "RAIZ", "RAMPA", "RASCA", "RASGO",
            "RAZON", "REBAÑ", "RECAL", "RECAR", "RECTA", "RECUR", "REINO", "RELOJ", "REMAR", "RENTA",
            "REPAS", "REPOL", "RESAL", "RESOL", "REUMA", "REZAR", "RIACH", "RIEGO", "RIÑON", "RITMO",
            "RIVAL", "ROBOT", "ROCIO", "RODEO", "RODAR", "RODIL", "RONCO", "RONDA", "ROSAL", "RUBIO",
            "RUEDA", "RUGIR", "RUMBA", "RUMOR", "SALIR", "SALSA", "SALTO", "SALUD", "SALVO", "SASTRE",
            "SAUCE", "SAUNA", "SAVIA", "SEÑAL", "SEÑOR", "SERRA", "SERVIR", "SEXTO", "SICLO", "SIDRA",
            "SIEGA", "SIGLO", "SIGNO", "SILBO", "SILLA", "SILVA", "SIRVE", "SIRVO", "SOBRE", "SOCIO",
            "SOLAR", "SOLAZ", "SORNA", "SUAVE", "SUBIR", "SUCIO", "SUECO", "SUELA", "SUEÑO", "SUFRE",
            "SUMAR", "SUPER", "SURCO", "TABAC", "TABLA", "TACHA", "TALLA", "TAPIZ", "TARDE", "TECHO",
            "TEJED", "TEJON", "TELAR", "TEMPO", "TENIA", "TENIS", "TENSO", "TEÑIR", "TIGRE", "TIMBA",
            "TIMON", "TINTO", "TIRAR", "TISSU", "TITAN", "TOBIL", "TOMAR", "TORPE", "TORRE", "TOSCO",
            "TOTAL", "TOTEM", "TRAER", "TRAMO", "TRAMPA", "TRATO", "TRECE", "TRIGA", "TRIGO", "TRINO",
            "TRIPA", "TRIST", "TROMP", "TRONO", "TROPA", "TROVA", "TRUCO", "TRUFA", "TUMOR", "TURBA",
            "TURNO", "TUTOR", "UNICO", "UNION", "URDIR", "URGIR", "USURP", "UVERO", "VALER", "VALOR",
            "VAPOR", "VARIA", "VASTO", "VEJEZ", "VELOZ", "VENDA", "VENIR", "VENTA", "VENUS", "VERAZ",
            "VERDE", "VERGA", "VERSO", "VIBRA", "VIDRI", "VIGOR", "VIRAR", "VIRGO", "VISTA", "VIUDA",
            "VOCAL", "VORAZ", "VUELT", "VULGO", "YERNO", "ZORRA", "ZURDO"
        };

        // ==================== PALABRAS DE 6 LETRAS ====================
        var palabras6 = new List<string>
        {
            "ABISMO", "ABOGAR", "ABORDO", "ABRIGO", "ABSORB", "ABUELA", "ABUELO", "ACABAN", "ACACIA", "ACATAR",
            "ACCION", "ACEITE", "ACENTO", "ACERCA", "ACLAMAR", "ACORDAR", "ACORDE", "ACUDIR", "ACUSAR", "ADAPTA",
            "ADICTO", "ADMIRA", "ADOPTA", "ADORAR", "ADORNO", "ADULTO", "AEREO", "AFECTO", "AFINAR", "AGITAR",
            "AGOBIA", "AGONIA", "AGOSTO", "AGOTAR", "AGRADO", "AGRAVA", "AGREGO", "AGUILA", "AHUMAR", "AJUSTE",
            "ALARMA", "ALCOBA", "ALDEANO", "ALEGRE", "ALEMAN", "ALERCE", "ALFARO", "ALFORJ", "ALGEBRA", "ALIADO",
            "ALIENTO", "ALINEA", "ALMEJA", "ALMENA", "ALQUIL", "ALTIVO", "ALTURA", "ALUMNO", "AMABLE", "AMASAR",
            "AMARGO", "AMBITO", "AMENAZ", "AMIGOS", "AMNIST", "AMPARO", "AMULTO", "ANCIAN", "ANFORA", "ANGOLA",
            "ANHELA", "ANILLO", "ANIMAL", "ANOCHE", "ANTENA", "ANZUELO", "AÑADIR", "APAGAR", "APAREC", "APARTE",
            "APELAR", "APENAS", "APLICA", "APORTA", "APOYAR", "APRETA", "APUNTE", "ARAÑA", "ARCANO", "ARDILLA",
            "ARDUO", "ARMADO", "ARMARIO", "ARMONIA", "ARRAIG", "ARROYO", "ARTESA", "ARVEJA", "ASALTO", "ASCEND",
            "ASEDIO", "ASFALTO", "ASIGNA", "ASOLAR", "ASPECTO", "ASTUTO", "ATAJAR", "ATAUD", "ATEISM", "ATLETA",
            "ATRAER", "ATRASO", "ATREZO", "AUDAZ", "AUNQUE", "AURORA", "AUSENTE", "AVANCE", "AVERNO", "AVERIA",
            "AVISAR", "AYUDAR", "AZUCAR", "AZUFRE", "BABOSA", "BAGAJE", "BAJADA", "BALCON", "BALLET", "BALOTA",
            "BAMBUS", "BANANO", "BANCAR", "BANDIR", "BARAJA", "BARRIO", "BASTON", "BATALLA", "BATIDO", "BATUTA",
            "BAYETA", "BELDAD", "BELICO", "BELLEZA", "BENEFI", "BESITO", "BIENES", "BISTUR", "BLANDO", "BLOQUE",
            "BOBINA", "BOCADO", "BODEGA", "BOLETO", "BONITO", "BORDAR", "BOSQUE", "BOTELLA", "BRAVIO", "BRECHA",
            "BRILLO", "BRINDA", "BRONCE", "BRUTAL", "BUCEAR", "BUENOS", "BUFALO", "BUFETE", "BUSCAR", "BUTACA",
            "CABEZA", "CABRON", "CACAOS", "CACTUS", "CADENA", "CADERA", "CAIMAN", "CALCAR", "CALIDO", "CALLAR",
            "CAMBIA", "CAMINO", "CAMION", "CAMISA", "CAMPAL", "CANCHA", "CANELA", "CANTAR", "CANTOR", "CAPOTA",
            "CAPTAR", "CARCEL", "CARGAR", "CARNAL", "CARNET", "CARONA", "CASERO", "CASETE", "CASINO", "CASONA",
            "CASTOR", "CAUDAL", "CAÑADA", "CAÑAMO", "CAÑON", "CELOSO", "CENIZA", "CENTRO", "CEREZA", "CERRAR",
            "CHALET", "CHARCO", "CHARRO", "CHISTE", "CHOCLO", "CIFRAR", "CIERVO", "CINCEL", "CIUDAD", "CLASIF",
            "CLONAR", "COBIJA", "COBRAR", "COCINA", "COLEGA", "COLGAR", "COLINA", "COLONO", "COMETA", "COMPAS",
            "CONCHA", "CONEJO", "CONGAL", "CONTAR", "COPIAR", "CORONA", "CORREA", "CORREO", "CORRER", "CORTEZ",
            "COSTAL", "COTEJO", "CRECER", "CRIADO", "CRISIS", "CRITICA", "CROTAL", "CUADRO", "CUBETA", "CUERDA",
            "CUERPO", "CUESTA", "CUIDAD", "CUMBRE", "CURIOSO", "DAÑINO", "DEBATE", "DECADA", "DELITO", "DEMORA",
            "DENTRO", "DERECHO", "DESEAR", "DESVIO", "DIABLO", "DIARIO", "DINERO", "DIRECTO", "DISCUR", "DIVINO",
            "DOBLAR", "DOCTOR", "DONCEL", "DORMIR", "DRAGON", "DUENDE", "EDIFICI", "ELEGIR", "EMBUDO", "EMPUJE",
            "ENCAJE", "ENCIMA", "ENDURE", "ENFERMA", "ENIGMA", "ENLACE", "ENORME", "ENSAYO", "ENTERA", "ENTRAR",
            "ENVIAR", "ESPADA", "ESPEJO", "ESPOSA", "ESPUMA", "ESTADO", "ESTERA", "ESTILO", "EUROPA", "EVALUA",
            "EXACTO", "EXAMEN", "EXISTE", "EXITO", "EXPERTO", "EXPRES", "FAISAN", "FALLAR", "FALSO", "FAMILIA",
            "FAMOSO", "FANTAS", "FARERO", "FATIGA", "FELINO", "FELIZ", "FENOME", "FIEBRE", "FIGURA", "FILMAR",
            "FILTRO", "FLECHA", "FLOTAR", "FLUIDO", "FOLLET", "FOMENT", "FUERTE", "FUGAZ", "FULGOR", "FUNDIR",
            "FUTBOL", "FUTURO", "GALEON", "GALOPE", "GANADO", "GARAJE", "GARFIO", "GEMIDO", "GENERO", "GENTIL",
            "GIGANT", "GOBERN", "GOLPEA", "GORILA", "GOTEAR", "GRITAR", "GRUESA", "GRUESO", "GRULLA", "GUANTE",
            "GUERRA", "GUIJON", "GUINEA", "GUISAR", "GURAMI", "HABITO", "HABLAR", "HALCON", "HAMBRE", "HELADO",
            "HERIDA", "HEROES", "HIERRO", "HIGADO", "HINCHA", "HONRAR", "HORNET", "HOSPED", "HUMANO", "HUMILD",
            "ILUDIR", "IMAGEN", "IMITAR", "IMPOST", "INCEND", "INCLUI", "INGRAT", "INICIO", "INSULA", "INVITA",
            "IRREAL", "JAURIA", "JETSKI", "JORNAL", "JORNADA", "JOVIAL", "JOYERO", "JUERGA", "JUGADA", "JUNGLA",
            "LADRON", "LAGUNA", "LATIDO", "LAUREL", "LEALTAD", "LENGUA", "LEVITA", "LEYEND", "LIBRAR", "LIGERO",
            "LIMITE", "LINAJE", "LLEGAR", "LLORAR", "LLUVIA", "LOCURA", "LONCHA", "LUBINA", "LUCERO", "LUCHAN",
            "LUGANO", "LUMBRE", "MADEJA", "MADURO", "MALEZA", "MAÑANA", "MANOJO", "MANTEL", "MANUAL", "MAÑOSO",
            "MAQUIN", "MARINA", "MARTES", "MASAJE", "MASCARA", "MEJILL", "MELENA", "MENTOR", "MERCED", "MERECE",
            "MESETA", "METAFORA", "MINUTO", "MIRADA", "MISERIA", "MITAD", "MOCION", "MODELO", "MOLINO", "MONEDA",
            "MONTAR", "MONTES", "MORADA", "MORDAZ", "MORENA", "MORENO", "MOTIVO", "MOVERSE", "MUCHOS", "MUGIDO",
            "MULETA", "NACION", "NAIPES", "NAVAJA", "NEBLINA", "NECTAR", "NEFAST", "NINGUN", "NITIDO", "NOCIVO",
            "NORMAL", "NOSTAL", "NUTRIA", "OBJETO", "OBLIGA", "OBRERO", "OCEANO", "OFENSA", "OFERTA", "OMITIR",
            "OPCION", "ORACLE", "ORDENA", "ORIGEN", "OSADIA", "OSCURO", "PAGODA", "PAISAJ", "PALOMA", "PANTAO",
            "PANTAN", "PARAJE", "PAREJO", "PARLAM", "PATADA", "PATRON", "PENSAR", "PERDIZ", "PEREZA", "PERFIL",
            "PERLAS", "PESADO", "PESCAR", "PETALO", "PLEGAR", "PLUMON", "POBLAR", "PODIUM", "POLACO", "POLICIA",
            "POLIZA", "PONCHO", "PORTAL", "POSADA", "POTAJE", "PRECIO", "PREMIA", "PRENSA", "PROEZA", "PROFAN",
            "PUEBLO", "PUÑADO", "QUEMAR", "QUERER", "QUINTO", "RABANO", "RAPIDO", "REBAÑO", "RECETA", "RECIBO",
            "REFLEJ", "REGALA", "REGION", "REINAR", "RELATO", "REMOLI", "RENDIJ", "REPASO", "REPOSA", "RETIRO",
            "RETRAT", "REUNE", "RIGIDO", "RITUAL", "ROBALO", "RODAJE", "RODEAR", "ROMPER", "RONDAR", "RUBICU",
            "RUGIDO", "RULETA", "SABANA", "SABIDO", "SABINO", "SACIAR", "SALIDA", "SALVAD", "SANGRE", "SARTEN",
            "SEISMO", "SELLAR", "SELVAS", "SEMANA", "SEMILL", "SENADO", "SENDER", "SERENO", "SESION", "SILBAR",
            "SILENC", "SILUET", "SIMPLE", "SIRENA", "SITUAR", "SOBERB", "SOCIAL", "SOLDAR", "SOLTAR", "SOLUCIO",
            "SOMBRE", "SONIDO", "SONREIR", "SOTANO", "SUBIDA", "SUEGRA", "SUEGRO", "SUJETO", "SULTAN", "SURGIR",
            "TACAÑO", "TAMBOR", "TAMPOC", "TAREAS", "TARIMA", "TEJADO", "TEJIDO", "TELON", "TEMIDO", "TIEMPO",
            "TIERNO", "TIMBRE", "TIPICO", "TIRANO", "TOALLA", "TOCADO", "TOLERA", "TOMATE", "TORNEO", "TORQUE",
            "TRAMAR", "TRAVIESO", "TESORO", "TESTIG", "TRISTE", "TRIUNF", "TRUENO", "TURRON", "UMERAL", "UNICOR",
            "UNIDAD", "URBANO", "USURPA", "VACIAR", "VALIDO", "VALIEN", "VARON", "VECINO", "VELADO", "VELERO",
            "VENADO", "VENCER", "VERDAD", "VIBRAR", "VIDRIO", "VIGILAR", "VIRGEN", "VIRTUD", "VISITA", "VITREO",
            "VIVEZA", "VOLCAN", "VOLVER", "VOMITO", "VORACE", "VUELTA", "VULGAR", "YEGUA", "ZARPAR", "ZORZAL"
        };

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
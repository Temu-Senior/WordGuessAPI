using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using WordGuessAPI.Data;
using WordGuessAPI.Services;

namespace WordGuessAPI.Hubs;

[Authorize]
public class GameHub : Hub
{
    private static GameLogic _gameLogic = new();
    private readonly AppDbContext _db;

    public GameHub(AppDbContext db)
    {
        _db = db;
    }

    public override async Task OnConnectedAsync()
    {
        // Opcional: enviar estado actual al reconectar
        await Clients.Caller.SendAsync("GameState", _gameLogic.GetState());
        await base.OnConnectedAsync();
    }

    [Authorize]
    public async Task StartGame(string difficulty)
    {
        var username = Context.User?.Identity?.Name ?? "Anónimo";
        // Obtener palabras de la BD según dificultad (por longitud)
        var words = await GetWordsByDifficulty(difficulty);
        _gameLogic.ResetGame(difficulty, words);
        await Clients.All.SendAsync("GameReset", _gameLogic.GetState());
    }

    private async Task<List<string>> GetWordsByDifficulty(string difficulty)
    {
        int length = difficulty switch
        {
            "easy" => 4,
            "normal" => 5,
            "hard" => 6,
            _ => 5
        };
        var words = await _db.Words
            .Where(w => w.Text.Length == length)
            .Select(w => w.Text.ToUpper())
            .ToListAsync();

        if (!words.Any())
        {
            // Palabras por defecto si no hay en BD
            words = difficulty switch
            {
                "easy" => new List<string> { "CASA", "PERRO", "GATO" },
                "normal" => new List<string> { "MUNDO", "SOL", "LUNA" },
                "hard" => new List<string> { "PROGRAMAR", "SERVIDOR", "BASE" }
            };
        }
        return words;
    }

    public async Task GuessLetter(char letter)
    {
        var result = _gameLogic.GuessLetter(letter);
        if (result.error != null)
        {
            await Clients.Caller.SendAsync("Error", result.error);
            return;
        }

        await Clients.All.SendAsync("GameUpdate", new
        {
            result.maskedWord,
            result.attemptsLeft,
            WrongGuesses = _gameLogic.WrongGuesses,
            GuessedLetters = _gameLogic.GuessedLetters,
            result.gameOver,
            Winner = _gameLogic.Winner
        });
    }

    public async Task GuessWord(string word)
    {
        var username = Context.User?.Identity?.Name ?? "Anónimo";
        var result = _gameLogic.GuessWord(word, username);
        if (result.error != null)
        {
            await Clients.Caller.SendAsync("Error", result.error);
            return;
        }

        await Clients.All.SendAsync("GameUpdate", new
        {
            result.maskedWord,
            AttemptsLeft = _gameLogic.AttemptsLeft,
            WrongGuesses = _gameLogic.WrongGuesses,
            GuessedLetters = _gameLogic.GuessedLetters,
            result.gameOver,
            Winner = _gameLogic.Winner,
            result.message
        });
    }

    [Authorize(Roles = "admin")]
    public async Task ResetGame()
    {
        // Reiniciar con dificultad normal por defecto
        await StartGame("normal");
    }
}
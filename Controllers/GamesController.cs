using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using WordGuessAPI.Data;
using WordGuessAPI.Models;

namespace WordGuessAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class GamesController : ControllerBase
{
    private readonly AppDbContext _context;

    public GamesController(AppDbContext context)
    {
        _context = context;
    }

    public class StartGameRequest
    {
        public string? Date { get; set; }
        public int? Length { get; set; }
    }

    public class AttemptRequest
    {
        public string Guess { get; set; } = string.Empty;
    }

    [HttpPost]
    public async Task<IActionResult> StartGame([FromBody] StartGameRequest? request)
    {
        var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        Word? word = null;

        if (!string.IsNullOrEmpty(request?.Date))
        {
            if (!DateTime.TryParse(request.Date, out var date))
                return BadRequest(new { success = false, message = "Invalid date format" });
            word = await _context.Words.FirstOrDefaultAsync(w => w.Date.HasValue && w.Date.Value.Date == date.Date);
            if (word == null)
                return BadRequest(new { success = false, message = "No word found for that date" });
        }
        else
        {
            word = await _context.Words.OrderBy(w => Guid.NewGuid()).FirstOrDefaultAsync();
            if (word == null)
                return BadRequest(new { success = false, message = "No words available" });
        }

        var game = new Game
        {
            UserId = userId,
            WordId = word.Id,
            IsCompleted = false,
            Attempts = 0
        };
        _context.Games.Add(game);
        await _context.SaveChangesAsync();

        return Ok(new { success = true, data = new { gameId = game.Id } });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetGame(int id)
    {
        var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var game = await _context.Games
            .Include(g => g.Word)
            .Include(g => g.AttemptsList.OrderBy(a => a.AttemptedAt))
            .FirstOrDefaultAsync(g => g.Id == id && g.UserId == userId);

        if (game == null)
            return NotFound(new { success = false, message = "Game not found" });

        return Ok(new { success = true, data = game });
    }

    [HttpPost("{id}/attempts")]
    public async Task<IActionResult> MakeAttempt(int id, [FromBody] AttemptRequest request)
    {
        var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var game = await _context.Games
            .Include(g => g.Word)
            .Include(g => g.AttemptsList)
            .FirstOrDefaultAsync(g => g.Id == id && g.UserId == userId);

        if (game == null)
            return NotFound(new { success = false, message = "Game not found" });
        if (game.IsCompleted)
            return BadRequest(new { success = false, message = "Game already completed" });

        var targetWord = game.Word!.Text.ToUpperInvariant();
        var guess = request.Guess.ToUpperInvariant();

        if (guess.Length != targetWord.Length)
            return BadRequest(new { success = false, message = $"Guess must be exactly {targetWord.Length} letters" });

        var evaluation = EvaluateGuess(guess, targetWord);

        var attempt = new Attempt
        {
            GameId = game.Id,
            Guess = request.Guess,
            Result = evaluation
        };
        _context.Attempts.Add(attempt);
        game.Attempts++;

        if (guess == targetWord)
        {
            game.IsCompleted = true;
            game.IsWon = true;
        }
        else if (game.Attempts >= 6)
        {
            game.IsCompleted = true;
            game.IsWon = false;
        }

        await _context.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            data = new
            {
                result = evaluation,
                gameCompleted = game.IsCompleted,
                won = game.IsWon,
                attemptsLeft = 6 - game.Attempts
            }
        });
    }

    [HttpGet("~/api/me/games")]
    public async Task<IActionResult> GetMyGames()
    {
        var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var games = await _context.Games
            .Where(g => g.UserId == userId)
            .OrderByDescending(g => g.CreatedAt)
            .Include(g => g.Word)
            .Select(g => new
            {
                g.Id,
                g.CreatedAt,
                g.IsCompleted,
                g.IsWon,
                g.Attempts,
                Word = g.Word!.Text
            })
            .ToListAsync();

        return Ok(new { success = true, data = games });
    }

    private string EvaluateGuess(string guess, string target)
    {
        var result = new List<object>();
        var targetChars = target.ToCharArray();
        var guessChars = guess.ToCharArray();
        var used = new bool[target.Length];

        for (int i = 0; i < guess.Length; i++)
        {
            if (guessChars[i] == targetChars[i])
            {
                result.Add(new { letter = guessChars[i].ToString(), status = "correct" });
                used[i] = true;
            }
            else
            {
                result.Add(new { letter = guessChars[i].ToString(), status = "absent" });
            }
        }

        for (int i = 0; i < guess.Length; i++)
        {
            if (((dynamic)result[i]).status == "correct") continue;

            for (int j = 0; j < target.Length; j++)
            {
                if (!used[j] && guessChars[i] == targetChars[j])
                {
                    result[i] = new { letter = guessChars[i].ToString(), status = "present" };
                    used[j] = true;
                    break;
                }
            }
        }

        return System.Text.Json.JsonSerializer.Serialize(result);
    }
}
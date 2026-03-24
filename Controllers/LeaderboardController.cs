using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WordGuessAPI.Data;

namespace WordGuessAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LeaderboardController : ControllerBase
{
    private readonly AppDbContext _context;

    public LeaderboardController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> GetLeaderboard()
    {
        var leaderboard = await _context.Users
            .Select(u => new
            {
                u.Username,
                Wins = u.Games.Count(g => g.IsWon),
                AvgAttempts = u.Games.Where(g => g.IsWon).Average(g => (double?)g.Attempts) ?? 0.0
            })
            .OrderByDescending(x => x.Wins)
            .ThenBy(x => x.AvgAttempts)
            .Take(20)
            .ToListAsync();

        return Ok(new { success = true, data = leaderboard });
    }
}
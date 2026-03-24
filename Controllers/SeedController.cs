using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WordGuessAPI.Data;
using WordGuessAPI.Models;

namespace WordGuessAPI.Controllers;

[ApiController]
[Route("api/_seed_demo")]
public class SeedController : ControllerBase
{
    private readonly AppDbContext _context;

    public SeedController(AppDbContext context)
    {
        _context = context;
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> SeedDemo()
    {
        var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var user = await _context.Users.FindAsync(userId);
        if (user == null || !user.IsAdmin)
            return Forbid();

        var demoWords = new[]
        {
            new Word { Text = "ruby", Difficulty = "easy", Date = DateTime.Today },
            new Word { Text = "sinatra", Difficulty = "medium", Date = DateTime.Today.AddDays(-1) },
            new Word { Text = "docker", Difficulty = "hard" }
        };
        await _context.Words.AddRangeAsync(demoWords);
        await _context.SaveChangesAsync();
        return Ok(new { success = true, message = "Demo words added" });
    }
}
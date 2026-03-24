using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using WordGuessAPI.Data;
using WordGuessAPI.Models;

namespace WordGuessAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WordsController : ControllerBase
{
    private readonly AppDbContext _context;

    public WordsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> GetWords([FromQuery] string? date, [FromQuery] string? difficulty)
    {
        var query = _context.Words.AsQueryable();
        if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, out var parsedDate))
            query = query.Where(w => w.Date.HasValue && w.Date.Value.Date == parsedDate.Date);
        if (!string.IsNullOrEmpty(difficulty))
            query = query.Where(w => w.Difficulty == difficulty.ToLower());

        var words = await query.ToListAsync();
        return Ok(new { success = true, data = words });
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> CreateWord([FromBody] Word word)
    {
        var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var user = await _context.Users.FindAsync(userId);
        if (user == null || !user.IsAdmin)
            return Forbid();

        if (string.IsNullOrWhiteSpace(word.Text))
            return BadRequest(new { success = false, message = "Word text is required" });

        _context.Words.Add(word);
        await _context.SaveChangesAsync();
        return Ok(new { success = true, data = word });
    }
}
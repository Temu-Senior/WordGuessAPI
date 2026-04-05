using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using WordGuessAPI.Data;
using WordGuessAPI.Models;

namespace WordGuessAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public AuthController(AppDbContext context, IConfiguration config, IWebHostEnvironment env)
    {
        _context = context;
        _config = config;
        _env = env;
    }

    public class RegisterRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class LoginRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        // Validaciones
        if (request.Username.Length < 3 || request.Username.Length > 20)
            return BadRequest(new { success = false, message = "Username must be 3-20 characters" });
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Username, @"^[a-zA-Z0-9_]+$"))
            return BadRequest(new { success = false, message = "Username can only contain letters, numbers and underscore" });
        if (request.Password.Length < 6)
            return BadRequest(new { success = false, message = "Password must be at least 6 characters" });

        if (await _context.Users.AnyAsync(u => u.Username == request.Username))
            return BadRequest(new { success = false, message = "Username already exists" });

        var isFirstUser = !await _context.Users.AnyAsync();

        var user = new User
        {
            Username = request.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            IsAdmin = isFirstUser
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return Ok(new { success = true, message = "User registered" });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == request.Username);
        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            return Unauthorized(new { success = false, message = "Invalid credentials" });

        var token = GenerateJwtToken(user);
        var expiryMinutes = double.Parse(_config["Jwt:ExpiryMinutes"]!);
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = !_env.IsDevelopment(), // Solo HTTPS en producción
            SameSite = SameSiteMode.Strict,
            Expires = DateTime.UtcNow.AddMinutes(expiryMinutes)
        };
        Response.Cookies.Append("token", token, cookieOptions);

        // También devolvemos el token en el cuerpo para compatibilidad con el frontend actual
        return Ok(new { success = true, data = new { message = "login successful", token } });
    }

    [HttpPost("logout")]
    [Authorize]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("token");
        return Ok(new { success = true, message = "logout successful" });
    }

    private string GenerateJwtToken(User user)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.ASCII.GetBytes(_config["Jwt:Key"]!);
        var expiryMinutes = double.Parse(_config["Jwt:ExpiryMinutes"]!);
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.IsAdmin ? "admin" : "user")
            }),
            Expires = DateTime.UtcNow.AddMinutes(expiryMinutes),
            Issuer = _config["Jwt:Issuer"],
            Audience = _config["Jwt:Audience"],
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }
}
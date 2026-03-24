using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Npgsql;                               // <-- Añadido
using WordGuessAPI.Data;
using WordGuessAPI.Models;

var builder = WebApplication.CreateBuilder(args);

// Leer la cadena de conexión (puede ser DATABASE_URL o la clásica)
var rawConnectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
                          ?? builder.Configuration.GetConnectionString("DefaultConnection");

// Convertir si es una URL estilo postgres://
var connectionString = rawConnectionString;
if (rawConnectionString != null && 
    (rawConnectionString.StartsWith("postgres://") || rawConnectionString.StartsWith("postgresql://")))
{
    var uri = new Uri(rawConnectionString);
    var npgsqlBuilder = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Database = uri.AbsolutePath.Trim('/'),
        Username = uri.UserInfo.Split(':')[0],
        Password = uri.UserInfo.Split(':')[1],
        SslMode = SslMode.Require,        // Render exige SSL
        TrustServerCertificate = true      // Opcional, evita problemas con certificados autofirmados
    };
    connectionString = npgsqlBuilder.ConnectionString;
}

// Registrar DbContext con PostgreSQL
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Swagger con JWT
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "WordGuessAPI", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Description = "Ingrese el token JWT: Bearer {token}",
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            new string[] {}
        }
    });
});

// JWT
var jwtKey = builder.Configuration["Jwt:Key"];
var key = Encoding.ASCII.GetBytes(jwtKey ?? throw new InvalidOperationException("JWT Key missing"));

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false;
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidateAudience = true,
        ValidAudience = builder.Configuration["Jwt:Audience"],
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero
    };
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Aplicar EnsureCreated y sembrar datos iniciales
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // Usar EnsureCreated en lugar de Migrate
    db.Database.EnsureCreated();   // <--- CAMBIADO

    if (!db.Words.Any())
    {
        db.Words.AddRange(new List<Word>
        {
            new Word { Text = "ruby", Difficulty = "easy" },
            new Word { Text = "sinatra", Difficulty = "medium" },
            new Word { Text = "docker", Difficulty = "hard" },
            new Word { Text = "wordle", Difficulty = "medium", Date = DateTime.Today }
        });
        db.SaveChanges();
    }

    if (!db.Users.Any())
    {
        var admin = new User
        {
            Username = "admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("123456"),
            IsAdmin = true
        };
        db.Users.Add(admin);
        db.SaveChanges();
    }
}

app.Run();
using Microsoft.EntityFrameworkCore;
using WordGuessAPI.Models;

namespace WordGuessAPI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users { get; set; }
    public DbSet<Word> Words { get; set; }
    public DbSet<Game> Games { get; set; }
    public DbSet<Attempt> Attempts { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

        modelBuilder.Entity<Game>()
            .HasOne(g => g.User)
            .WithMany(u => u.Games)
            .HasForeignKey(g => g.UserId);

        modelBuilder.Entity<Game>()
            .HasOne(g => g.Word)
            .WithMany()
            .HasForeignKey(g => g.WordId);

        modelBuilder.Entity<Attempt>()
            .HasOne(a => a.Game)
            .WithMany(g => g.AttemptsList)
            .HasForeignKey(a => a.GameId);
    }
}
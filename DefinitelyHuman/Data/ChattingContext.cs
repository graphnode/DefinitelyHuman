using Microsoft.EntityFrameworkCore;

namespace DefinitelyHuman.Data;

public class ChattingContext : DbContext
{
    public DbSet<Message> Messages { get; set; }

    public string DbPath { get; }

    public ChattingContext()
    {
        const Environment.SpecialFolder folder = Environment.SpecialFolder.LocalApplicationData;
        string path = Environment.GetFolderPath(folder);
        DbPath = Path.Join(path, "chatting.db");
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite($"Data Source={DbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Message>()
            .HasIndex(m => new { m.Channel, m.Timestamp });
    }
}

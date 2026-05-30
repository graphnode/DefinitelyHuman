using Microsoft.EntityFrameworkCore;

namespace DefinitelyHuman.Data;

public class ChattingContext : DbContext
{
    public DbSet<Message> Messages { get; set; }

    private readonly string _dbPath;

    public ChattingContext()
    {
        const Environment.SpecialFolder folder = Environment.SpecialFolder.LocalApplicationData;
        string path = Environment.GetFolderPath(folder);
        _dbPath = Path.Join(path, "chatting.db");
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite($"Data Source={_dbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Message>()
            .HasIndex(m => new { m.Channel, m.Timestamp });
    }
}

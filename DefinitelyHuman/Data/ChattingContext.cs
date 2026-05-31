using Microsoft.EntityFrameworkCore;

namespace DefinitelyHuman.Data;

public class ChattingContext : DbContext
{
    public DbSet<Message> Messages { get; set; }
    public DbSet<AgentEvent> AgentEvents { get; set; }
    public DbSet<CachedLinkPreview> CachedLinkPreviews { get; set; }

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

        modelBuilder.Entity<AgentEvent>(e =>
        {
            e.HasIndex(a => new { a.Channel, a.Timestamp });
            // Store the enum as text for a legible DB.
            e.Property(a => a.Kind).HasConversion<string>();
            // Optional link to the message the event produced; messages are never deleted.
            e.HasOne<Message>()
                .WithMany()
                .HasForeignKey(a => a.MessageId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }
}

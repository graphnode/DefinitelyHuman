using Microsoft.EntityFrameworkCore;

namespace DefinitelyHuman.Data;

public class ChattingContext : DbContext
{
    public DbSet<Message> Messages { get; set; }
    public DbSet<AgentEvent> AgentEvents { get; set; }
    public DbSet<CachedLinkPreview> CachedLinkPreviews { get; set; }
    public DbSet<Entity> Entities { get; set; }
    public DbSet<EntityAlias> EntityAliases { get; set; }
    public DbSet<Fact> Facts { get; set; }
    public DbSet<FactEvidence> FactEvidence { get; set; }
    public DbSet<MemoryRun> MemoryRuns { get; set; }
    public DbSet<ProcessedMessage> ProcessedMessages { get; set; }
    public DbSet<Setting> Settings { get; set; }

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

    /// <summary>
    /// Creates the database, and in an existing one any tables and indexes added to the model since.
    /// </summary>
    public void EnsureSchema()
    {
        if (Database.EnsureCreated())
            return;

        // ponytail: EnsureCreated does nothing for an existing database, so the model's own
        // create script is replayed with IF NOT EXISTS. That adds new tables and indexes but
        // never a column to an existing table; switch to EF migrations when one is needed.
        string script = Database.GenerateCreateScript()
            .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ")
            .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ")
            .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ");
        Database.ExecuteSqlRaw(script.Replace("{", "{{").Replace("}", "}}"));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Fact>(e =>
        {
            e.HasIndex(f => f.SubjectEntityId);
            e.HasIndex(f => f.ObjectEntityId);
            e.Property(f => f.Source).HasConversion<string>();
        });
        modelBuilder.Entity<FactEvidence>(e =>
        {
            e.HasKey(x => new { x.FactId, x.MessageId });
            e.HasIndex(x => x.MessageId);
        });
        modelBuilder.Entity<EntityAlias>().HasIndex(a => a.EntityId);
        modelBuilder.Entity<MemoryRun>().Property(r => r.Kind).HasConversion<string>();

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

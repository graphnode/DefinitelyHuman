using System.ComponentModel.DataAnnotations;

namespace DefinitelyHuman.Data;

public class Message
{
    public int MessageId { get; set; }
    [MaxLength(64)] public required string Channel { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    [MaxLength(64)] public required string Nick { get; set; }
    [MaxLength(2048)] public required string Text { get; set; }
    public bool IsOwnMessage { get; set; }
}

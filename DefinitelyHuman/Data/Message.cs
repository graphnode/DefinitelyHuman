using System.ComponentModel.DataAnnotations;

namespace DefinitelyHuman.Data;

public class Message
{
    public int MessageId { get; init; }
    [MaxLength(64)] public required string Channel { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    [MaxLength(64)] public required string Nick { get; init; }
    [MaxLength(2048)] public required string Text { get; init; }
    public bool IsOwnMessage { get; init; }
}

using System.ComponentModel.DataAnnotations;

namespace DefinitelyHuman.Data;

public class CachedLinkPreview
{
    [Key]
    public required string Url { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? ImageUrl { get; init; }
    public DateTime FetchedAt { get; init; } = DateTime.UtcNow;
}

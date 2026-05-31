using System.ComponentModel.DataAnnotations;

namespace DefinitelyHuman.Data;

public enum AgentEventKind
{
    Unknown,
    Decision,
    Thinking,
    Error,
    ToolCall,
}

/// <summary>
/// A timestamped record of something the agent did or thought (a glance decision, a chunk of
/// extended-thinking, an error, and later MCP tool calls). Stored alongside <see cref="Message"/>
/// and merged with it by timestamp into the dashboard timeline.
/// </summary>
public class AgentEvent
{
    public int AgentEventId { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    [MaxLength(64)] public required string Channel { get; init; }

    public AgentEventKind Kind { get; init; }

    /// <summary>One-line description, always shown in the timeline.</summary>
    [MaxLength(512)] public required string Summary { get; init; }

    /// <summary>Optional long-form detail: full thinking text, or later tool args+results as JSON.</summary>
    public string? Detail { get; init; }

    /// <summary>The chat message this event produced, if any (e.g. the reply a glance sent).</summary>
    public int? MessageId { get; init; }
}

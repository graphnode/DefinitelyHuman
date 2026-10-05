using System.ComponentModel.DataAnnotations;

namespace DefinitelyHuman.Data;

/// <summary>Someone or something the bot knows about: a person (nick), project, tool, place or topic.</summary>
public class Entity
{
    public int EntityId { get; init; }
    [MaxLength(80)] public required string Name { get; set; }

    /// <summary>person, project, tool, place, topic or other.</summary>
    [MaxLength(16)] public required string Type { get; set; }

    /// <summary>A short profile, rewritten by the curation pass from the current facts.</summary>
    public string? Summary { get; set; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>When the curation pass last went over this entity; facts created later make it due again.</summary>
    public DateTime? CuratedAt { get; set; }
}

/// <summary>
/// A name an entity goes by, lowercased. Every entity has at least its own name here, so
/// finding one by any of its names is a single lookup.
/// </summary>
public class EntityAlias
{
    [Key, MaxLength(80)] public required string Alias { get; init; }
    public int EntityId { get; set; }
}

public enum FactSource
{
    /// <summary>Read straight out of the chat log.</summary>
    Extracted,
    /// <summary>Several facts folded into one by the curation pass.</summary>
    Merged,
    /// <summary>A conclusion the curation pass drew from other facts.</summary>
    Derived,
}

/// <summary>
/// One thing the bot remembers, as a self-contained sentence about <see cref="SubjectEntityId"/>.
/// With an <see cref="ObjectEntityId"/> it is also an edge between two entities. Facts are never
/// deleted: one that stops being true gets an <see cref="InvalidatedAt"/>.
/// </summary>
public class Fact
{
    public int FactId { get; init; }
    [MaxLength(512)] public required string Text { get; set; }
    public int SubjectEntityId { get; set; }
    public int? ObjectEntityId { get; set; }

    /// <summary>The channel it was learned in.</summary>
    [MaxLength(64)] public required string Channel { get; init; }

    public FactSource Source { get; init; }

    /// <summary>When it was said (the earliest message behind it), as opposed to when it was recorded.</summary>
    public DateTime ValidFrom { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public DateTime? InvalidatedAt { get; set; }

    /// <summary>The fact that replaced this one, if it was merged or contradicted.</summary>
    public int? SupersededByFactId { get; set; }
}

/// <summary>A chat line a fact rests on. Merged and derived facts inherit the lines of the facts they came from.</summary>
public class FactEvidence
{
    public int FactId { get; init; }
    public int MessageId { get; init; }
}

public enum MemoryRunKind
{
    Extraction,
    Curation,
}

/// <summary>One model pass of the memory system: an extraction over a slice of log, or a night's curation.</summary>
public class MemoryRun
{
    public int MemoryRunId { get; init; }
    public MemoryRunKind Kind { get; init; }
    [MaxLength(64)] public string? Channel { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    [MaxLength(64)] public required string Model { get; init; }
    public bool Failed { get; set; }

    /// <summary>One line: what the run covered and produced, or the error.</summary>
    [MaxLength(512)] public string Summary { get; set; } = "";

    /// <summary>Long form, e.g. the curation pass's suggestions of entities to merge.</summary>
    public string? Detail { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
}

/// <summary>
/// Marks a chat line as already read by extraction. A line without a row here is still to do,
/// which holds for logs imported later too (message ids are not chronological).
/// </summary>
public class ProcessedMessage
{
    [Key] public int MessageId { get; init; }
    public int MemoryRunId { get; init; }
}

/// <summary>A setting changed from the dashboard (see <c>AppSettings</c>); absent means the default.</summary>
public class Setting
{
    [Key, MaxLength(64)] public required string Key { get; init; }
    public required string Value { get; set; }
}

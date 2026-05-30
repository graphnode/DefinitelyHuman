using System.Collections.Concurrent;

namespace DefinitelyHuman.Agent;

/// <summary>
/// An always-on, in-memory ring buffer of the agent's decisions (why it glanced, ignored,
/// stayed silent, or replied) for display on the dashboard. Capturing here is independent of
/// the console <c>logReasoning</c> flag — that only controls console echo.
/// </summary>
public sealed class AgentLog
{
    public enum Kind { Decision, Thinking, Error }

    public record Entry(DateTime At, Kind Kind, string Text);

    private const int Max = 300;
    private readonly ConcurrentQueue<Entry> _entries = new();

    /// <summary>Raised after an entry is added so the dashboard can refresh.</summary>
    public event Action? Updated;

    public void Log(Kind kind, string text)
    {
        _entries.Enqueue(new Entry(DateTime.UtcNow, kind, text));
        while (_entries.Count > Max && _entries.TryDequeue(out _)) { }
        Updated?.Invoke();
    }

    public IReadOnlyList<Entry> Recent() => _entries.ToArray();
}

using System.Threading.Channels;
using DefinitelyHuman.Data;

namespace DefinitelyHuman.Agent;

/// <summary>
/// Persists the agent's events (glance decisions, extended-thinking, errors, and later MCP tool
/// calls) to the database, where they're merged with the chat log into the dashboard timeline.
///
/// <see cref="Log"/> is synchronous and non-blocking (safe to call from inside the agent's locks):
/// it stamps the event and hands it to a channel. A single background task drains the channel,
/// writing events to SQLite in order, then raises <see cref="Updated"/> for live UI refresh.
/// </summary>
public sealed class AgentLog : IDisposable
{
    private readonly string _channel;
    private readonly ILogger<AgentLog> _logger;
    private readonly Channel<AgentEvent> _queue = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _drain;

    /// <summary>Raised after an event is committed so the dashboard can refresh.</summary>
    public event Action? Updated;

    public AgentLog(string channel, ILogger<AgentLog> logger)
    {
        _channel = channel;
        _logger = logger;
        _drain = Task.Run(DrainAsync);
    }

    /// <summary>
    /// Records an agent event. Non-blocking — the event is persisted by the drain task.
    /// </summary>
    /// <param name="kind">What kind of event this is.</param>
    /// <param name="summary">One-line description, always shown in the timeline.</param>
    /// <param name="detail">Optional long-form detail (full thinking, tool args+results).</param>
    /// <param name="messageId">The chat message this event produced, if any.</param>
    /// <param name="at">Override the timestamp (defaults to now); use to place an event just before the message it produced.</param>
    public void Log(AgentEventKind kind, string summary, string? detail = null, int? messageId = null, DateTime? at = null)
    {
        var evt = new AgentEvent
        {
            Timestamp = at ?? DateTime.UtcNow,
            Channel = _channel,
            Kind = kind,
            Summary = Truncate(summary, 512),
            Detail = detail,
            MessageId = messageId,
        };

        // Unbounded channel: TryWrite only fails after Complete(), which we only call on dispose.
        _queue.Writer.TryWrite(evt);
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var evt in _queue.Reader.ReadAllAsync())
            {
                try
                {
                    await using var db = new ChattingContext();
                    db.AgentEvents.Add(evt);
                    await db.SaveChangesAsync();
                    Updated?.Invoke();
                }
                catch (Exception ex)
                {
                    // A persistence failure must not kill the drain loop.
                    _logger.LogError(ex, "Failed to persist agent event");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent log drain loop stopped");
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try { _drain.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort flush on shutdown */ }
    }
}

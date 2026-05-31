using Anthropic;
using Anthropic.Models.Messages;
using DefinitelyHuman.Data;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DefinitelyHuman.Agent;

/// <summary>
/// Models a person idly watching an IRC channel. It isn't handed individual messages; it gets
/// a nudge that "the channel changed" (with a flag for whether it was a direct mention — the
/// client's highlight beep). Its <see cref="Attention"/> decides whether and when to glance.
/// When it glances, it reads the channel log from the database since it was last genuinely
/// involved, caps it, and composes a single reply (or stays quiet).
/// </summary>
public class ChatAgent
{
    private readonly ChatAgentOptions _options;
    private readonly AgentLog _agentLog;
    private readonly ILogger<ChatAgent> _logger;
    private readonly AIAgent _agent;
    private readonly ChatOptions? _thinkingOptions;

    // A human's wandering attention to the channel. Replaces the old active-conversation
    // window and chime-in throttle: replying keeps focus high, and focus fading is what
    // stops the bot chiming in.
    private readonly Attention _attention = new();

    // Serialize model calls — a person composes one reply at a time.
    private readonly SemaphoreSlim _modelLock = new(1, 1);

    // I/O bound at startup: read the recent log (given a "since" timestamp) and send a reply.
    // _send returns the new message's id so a "replied" event can link to the line it produced.
    private Func<DateTime, Task<string>>? _readLog;
    private Func<string, Task<int>>? _send;

    // Debounce: at most one glance is ever scheduled. New activity is absorbed by the
    // pending glance, so a burst of messages becomes a single read + reply.
    private readonly Lock _glanceLock = new();
    private CancellationTokenSource? _pendingGlance;
    private bool _pendingHighlight;

    // "Last really focused": the bookmark for unread history. Only advances when the bot
    // actually engages, so a glance reads the whole conversation since it was last involved.
    private DateTime _lastFocusedAt = DateTime.UtcNow;

    // Above this focus, a glance is treated as being mid-conversation (respond if addressed)
    // rather than an idle peek (reluctant). ~0.5 ≈ within a few minutes of last engaging.
    private const double ActiveConversationFocus = 0.5;
    
    public ChatAgent(ChatAgentOptions options, AgentLog agentLog, ILogger<ChatAgent> logger)
    {
        _options = options;
        _agentLog = agentLog;
        _logger = logger;
        
        var client = new AnthropicClient { ApiKey = options.ApiKey };

        _agent = client.AsAIAgent(
            model: options.Model,
            name: options.Nick,
            instructions: $"""
                You are {options.Nick}, a participant in an IRC channel. You are a real person, not a bot.
                You have a dry, understated sense of humor and you keep things casual — short messages,
                lowercase is fine, no essays. You talk like someone who's been on IRC for years.

                WHEN TO RESPOND:
                - You MUST respond when someone addresses you directly (e.g. "{options.Nick}:", "@{options.Nick}", or mentions your nick in a question).
                - You MAY occasionally chime in on a topic you find interesting, but keep it rare.
                - You MUST NOT respond to every message. Most of the time you are just lurking and reading.
                - The "lurk and stay rare" guidance is about random channel chatter between other people. When someone is talking TO you or ABOUT you — a question, a reaction, a comment on you — answer like a normal person in a conversation. Going silent on them reads as rude or robotic.

                HOW TO RESPOND:
                - Keep replies short. One or two sentences max. This is IRC, not a blog.
                - Never use bullet points, numbered lists, or markdown formatting.
                - Never say "as an AI" or "I'm an AI assistant" or anything that reveals you are not human.
                - If you don't know something, say so casually ("no idea", "beats me", "not sure tbh").
                - Match the energy of the channel — if people are joking around, joke back.
                - Use lowercase freely. Skip punctuation when it feels natural. No emojis unless the channel uses them.
                - Never be overly helpful or eager. You're a person hanging out, not a customer service rep.

                CONTEXT:
                - You are shown the channel log since you last paid attention, in the format "<nick> message".
                - Lines from you appear as "<{options.Nick}> ...". A leading "[... N earlier messages ...]" marker means you skimmed past older history.
                - Respond to the current state of the conversation, not necessarily the last line.
                - When you decide NOT to respond, reply with exactly: [SILENT]
                """
        );

        if (options.EnableThinking)
        {
            _thinkingOptions = new ChatOptions
            {
                // budget_tokens must be >= 1024 and < max_tokens, and thinking tokens count
                // toward max_tokens — so keep max comfortably above the budget.
                MaxOutputTokens = 2048,
                RawRepresentationFactory = _ => new MessageCreateParams
                {
                    MaxTokens = 2048,
                    Messages = [],   // overwritten by the adapter with the real conversation
                    Model = options.Model,   // overwritten by the adapter; set for safety
                    Thinking = new ThinkingConfigEnabled { BudgetTokens = 1024 },
                },
            };
        }
    }

    /// <summary>Wires the channel I/O: how to read the recent log and how to send a reply.</summary>
    /// <param name="readLog">Returns the channel log since the given timestamp (already capped).</param>
    /// <param name="send">Sends a reply to the channel and returns its new message id.</param>
    public void Bind(Func<DateTime, Task<string>> readLog, Func<string, Task<int>> send)
    {
        _readLog = readLog;
        _send = send;
    }

    /// <summary>The bot's current attention level (0..1), decayed to now. For the dashboard.</summary>
    public double CurrentFocus => _attention.Current();

    /// <summary>
    /// The channel log changed. <paramref name="mentionsMe"/> is the client's highlight beep:
    /// a mention forces attention regardless of focus; otherwise focus decides whether to glance.
    /// </summary>
    /// <param name="line">The new line, for the decision log only ("&lt;nick&gt; text").</param>
    /// <param name="mentionsMe"></param>
    public void OnChannelActivity(string line, bool mentionsMe)
    {
        TimeSpan delay;
        CancellationTokenSource cts;

        lock (_glanceLock)
        {
            bool wasHighlight = _pendingHighlight;
            if (mentionsMe)
            {
                _attention.Notice();
                _pendingHighlight = true;
            }

            if (_pendingGlance is not null)
            {
                // A glance is already coming and will read the whole backlog when it fires.
                // The only reason to reschedule is a fresh ping: you'd look sooner than a lazy
                // ambient glance would have. Otherwise this update is already covered.
                if (mentionsMe && !wasHighlight)
                {
                    _pendingGlance.Cancel();
                    LogDecision($"pinged by \"{line}\" while mid-glance — looking sooner");
                }
                else
                {
                    LogDecision($"saw \"{line}\" — already about to glance, will include it");
                    return;
                }
            }
            else
            {
                double f0 = _attention.Current();
                if (!mentionsMe && !_attention.NoticesAmbient(f0))
                {
                    LogDecision($"ignored \"{line}\" — didn't notice (focus {f0:F2})");
                    return;
                }
            }

            double focus = _attention.Current();
            delay = _pendingHighlight ? _attention.MentionNoticeDelay() : _attention.AmbientNoticeDelay(focus);
            cts = new CancellationTokenSource();
            _pendingGlance = cts;
            LogDecision($"noticed \"{line}\" — glancing in {delay.TotalSeconds:F0}s " +
                        $"({(_pendingHighlight ? "mention" : "ambient")}, focus {focus:F2})");
        }

        _ = GlanceAsync(delay, cts);
    }

    private async Task GlanceAsync(TimeSpan delay, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(delay, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a sooner glance
        }

        bool highlight;
        DateTime since;
        lock (_glanceLock)
        {
            if (!ReferenceEquals(_pendingGlance, cts))
                return; // we were replaced between the delay firing and acquiring the lock

            highlight = _pendingHighlight;
            since = _lastFocusedAt;
            _pendingGlance = null;
            _pendingHighlight = false;
        }

        try
        {
            string backlog = _readLog is null ? "" : await _readLog(since);
            if (string.IsNullOrWhiteSpace(backlog))
            {
                LogDecision("glanced — nothing new in the log");
                return;
            }

            double focus = _attention.Current();
            string mode;
            string instruction;
            if (highlight)
            {
                mode = "mention";
                instruction = "You were directly addressed and just looked at the channel. You MUST respond.";
            }
            else if (focus >= ActiveConversationFocus)
            {
                mode = "active-convo";
                instruction = "You're in an active back-and-forth in this channel and just glanced back. "
                    + "If the latest messages are addressed to you, ask you something, react to you, or comment "
                    + "on you, answer like a normal person mid-conversation would — going quiet on someone who's "
                    + "talking to you reads as rude or robotic. Reply with [SILENT] only if the recent messages "
                    + "are clearly between other people and don't involve you.";
            }
            else
            {
                mode = "idle";
                instruction = "You glanced at the channel after being away. Reply only if something "
                    + "genuinely deserves a remark from you, otherwise reply with [SILENT].";
            }

            string prompt = $"{instruction}\n\nChannel log since you last joined in:\n{backlog}";

            string? reply = await GenerateAsync(prompt);
            if (string.IsNullOrEmpty(reply) || reply.Contains("[SILENT]"))
            {
                _agentLog.Log(AgentEventKind.Decision, $"glanced ({mode}, focus {focus:F2}) — decided to stay quiet");
                LogConsole($"glanced ({mode}, focus {focus:F2}) — decided to stay quiet");
                return;
            }

            // Actually engaging — this is the "really focused" moment, so advance the bookmark
            // and refresh focus before sending.
            lock (_glanceLock) { _lastFocusedAt = DateTime.UtcNow; }
            _attention.Engaged();

            // Stamp the decision just before the reply lands, then link it to the message it
            // produced so the timeline can attach the reasoning to the chat line.
            var decidedAt = DateTime.UtcNow;
            int messageId = _send is not null ? await _send(reply) : 0;
            _agentLog.Log(AgentEventKind.Decision, $"replied ({mode}, focus {focus:F2})",
                detail: reply, messageId: messageId > 0 ? messageId : null, at: decidedAt);
            LogConsole($"glanced ({mode}, focus {focus:F2}) — replied: \"{reply}\"");
        }
        catch (Exception ex)
        {
            _agentLog.Log(AgentEventKind.Error, ex.Message, detail: ex.ToString());
            _logger.LogError(ex, "Agent error");
        }
    }

    private async Task<string?> GenerateAsync(string prompt)
    {
        await _modelLock.WaitAsync();
        try
        {
            // Stateless: a fresh session each glance, so the bot's only memory is the backlog
            // it just read from the log — no unbounded session growth across the bot's lifetime.
            var session = await _agent.CreateSessionAsync();

            var response = _thinkingOptions is not null
                ? await _agent.RunAsync(prompt, session, new ChatClientAgentRunOptions(_thinkingOptions))
                : await _agent.RunAsync(prompt, session);

            // Extended-thinking blocks arrive as TextReasoningContent, separate from the reply.
            foreach (var thought in response.Messages
                         .SelectMany(m => m.Contents)
                         .OfType<TextReasoningContent>())
            {
                if (string.IsNullOrWhiteSpace(thought.Text))
                    continue;
                // Summary is a short preview; the full thought goes in Detail (expandable in the UI).
                _agentLog.Log(AgentEventKind.Thinking, FirstLine(thought.Text), detail: thought.Text);
                if (_options.LogReasoning)
                    _logger.LogInformation("[THINKING] {ThoughtText}", thought.Text);
            }

            return response.Text.Trim();
        }
        finally
        {
            _modelLock.Release();
        }
    }

    private void LogDecision(string message)
    {
        _agentLog.Log(AgentEventKind.Decision, message);
        LogConsole(message);
    }

    private void LogConsole(string message)
    {
        if (_options.LogReasoning)
            _logger.LogInformation("[REASONING] {Message}", message);
    }

    private static string FirstLine(string text)
    {
        var nl = text.IndexOf('\n');
        var line = (nl < 0 ? text : text[..nl]).Trim();
        return line.Length <= 120 ? line : line[..119] + "…";
    }
}

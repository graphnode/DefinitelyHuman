using Anthropic;
using Anthropic.Models.Messages;
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
    private readonly AIAgent _agent;
    private readonly bool _logReasoning;
    private readonly AgentLog _log;
    private readonly ChatOptions? _thinkingOptions;

    // A human's wandering attention to the channel. Replaces the old active-conversation
    // window and chime-in throttle: replying keeps focus high, and focus fading is what
    // stops the bot chiming in.
    private readonly Attention _attention = new();

    // Serialize model calls — a person composes one reply at a time.
    private readonly SemaphoreSlim _modelLock = new(1, 1);

    // I/O bound at startup: read the recent log (given a "since" timestamp) and send a reply.
    private Func<DateTime, Task<string>>? _readLog;
    private Func<string, Task>? _send;

    // Debounce: at most one glance is ever scheduled. New activity is absorbed by the
    // pending glance, so a burst of messages becomes a single read + reply.
    private readonly object _glanceLock = new();
    private CancellationTokenSource? _pendingGlance;
    private bool _pendingHighlight;

    // "Last really focused": the bookmark for unread history. Only advances when the bot
    // actually engages, so a glance reads the whole conversation since it was last involved.
    private DateTime _lastFocusedAt = DateTime.UtcNow;

    // Above this focus, a glance is treated as being mid-conversation (respond if addressed)
    // rather than an idle peek (reluctant). ~0.5 ≈ within a few minutes of last engaging.
    private const double ActiveConversationFocus = 0.5;

    /// <param name="apiKey">The API key for accessing the Claude API.</param>
    /// <param name="nick">The nickname of the agent.</param>
    /// <param name="log">Captures the agent's decisions for the dashboard.</param>
    /// <param name="model">The model to use for the agent. Defaults to "claude-haiku-4-5-20251001".</param>
    /// <param name="enableThinking">
    /// When true, requests Claude's extended thinking. This consumes extra (output) tokens
    /// on every glance the agent makes — including ones that stay silent.
    /// </param>
    /// <param name="logReasoning">
    /// When true, also echoes the decision log and the model's thinking to the console.
    /// The dashboard log is always captured regardless; only <paramref name="enableThinking"/> costs tokens.
    /// </param>
    public ChatAgent(string apiKey, string nick, AgentLog log, string model = "claude-haiku-4-5-20251001",
                     bool enableThinking = false, bool logReasoning = false)
    {
        _log = log;
        _logReasoning = logReasoning;

        var client = new AnthropicClient { ApiKey = apiKey };

        _agent = client.AsAIAgent(
            model: model,
            name: nick,
            instructions: $"""
                You are {nick}, a participant in an IRC channel. You are a real person, not a bot.
                You have a dry, understated sense of humor and you keep things casual — short messages,
                lowercase is fine, no essays. You talk like someone who's been on IRC for years.

                WHEN TO RESPOND:
                - You MUST respond when someone addresses you directly (e.g. "{nick}:", "@{nick}", or mentions your nick in a question).
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
                - Lines from you appear as "<{nick}> ...". A leading "[... N earlier messages ...]" marker means you skimmed past older history.
                - Respond to the current state of the conversation, not necessarily the last line.
                - When you decide NOT to respond, reply with exactly: [SILENT]
                """
        );

        if (enableThinking)
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
                    Model = model,   // overwritten by the adapter; set for safety
                    Thinking = new ThinkingConfigEnabled { BudgetTokens = 1024 },
                },
            };
        }
    }

    /// <summary>Wires the channel I/O: how to read the recent log and how to send a reply.</summary>
    /// <param name="readLog">Returns the channel log since the given timestamp (already capped).</param>
    /// <param name="send">Sends a reply to the channel.</param>
    public void Bind(Func<DateTime, Task<string>> readLog, Func<string, Task> send)
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
                LogDecision($"glanced ({mode}, focus {focus:F2}) — decided to stay quiet");
                return;
            }

            // Actually engaging — this is the "really focused" moment, so advance the bookmark
            // and refresh focus before sending.
            lock (_glanceLock) { _lastFocusedAt = DateTime.UtcNow; }
            _attention.Engaged();
            LogDecision($"glanced ({mode}, focus {focus:F2}) — replied: \"{reply}\"");

            if (_send is not null)
                await _send(reply);
        }
        catch (Exception ex)
        {
            _log.Log(AgentLog.Kind.Error, ex.Message);
            Console.WriteLine($"Agent error: {ex.Message} {ex.StackTrace}");
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
                _log.Log(AgentLog.Kind.Thinking, thought.Text);
                if (_logReasoning)
                    Console.WriteLine($"[THINKING] {thought.Text}");
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
        _log.Log(AgentLog.Kind.Decision, message);
        if (_logReasoning)
            Console.WriteLine($"[REASONING] {message}");
    }
}

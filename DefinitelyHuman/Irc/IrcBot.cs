using System.Text;
using DefinitelyHuman.Data;
using Microsoft.EntityFrameworkCore;
using NetIRC;
using NetIRC.Connection;
using NetIRC.Messages;

namespace DefinitelyHuman.Irc;

public class IrcBot : IDisposable
{
    private readonly Client _client;
    private readonly Random _rng = new();
    
    private readonly IrcBotOptions _options;
    private readonly ILogger<IrcBot> _logger;

    private const int MaxReplyLength = 400;

    // A bouncer replays what we missed in a burst right after we connect, with no timestamps.
    // Anything arriving inside this window is logged but doesn't nudge the agent.
    private static readonly TimeSpan ReplayWindow = TimeSpan.FromSeconds(10);
    private DateTime _registeredAt;

    /// <summary>
    /// A channel's log changed. Arguments: the channel, the new line ("&lt;nick&gt; text", for
    /// logging), and the highlight "beep": the new line mentions the bot.
    /// </summary>
    public event Action<string, string, bool>? ChannelActivity;

    /// <summary>The set of channels the bot is in changed (joined, left, kicked, or disconnected).</summary>
    public event Action? ChannelsChanged;

    // Channels the bot is in right now, as the server tells it. With a bouncer this includes the
    // ones the bouncer rejoins for us on connect.
    private readonly HashSet<string> _joined = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after a message is written to the log, so the web dashboard can refresh.</summary>
    public event Func<Task>? MessageLogged;

    /// <summary>The configured home channel: joined on connect and shown first in the dashboard.</summary>
    public string Channel => _options.Channel;

    /// <summary>The channels the bot is in right now.</summary>
    public IReadOnlyList<string> JoinedChannels
    {
        get { lock (_joined) return [.. _joined]; }
    }
    public string Nick => _options.Nick;

    /// <summary>The host:port this process connects to: the IRC server, or a bouncer in front of it.</summary>
    public string Endpoint => $"{_options.Host}:{_options.Port}";

    /// <summary>
    /// Registered with <see cref="Endpoint"/> and not dropped since. Says nothing about a
    /// bouncer's own connection to the network; <see cref="LastLineAt"/> is the evidence for that.
    /// </summary>
    public bool IsConnected { get; private set; }

    /// <summary>When the last channel line from someone else arrived, or null if none has since startup.</summary>
    public DateTime? LastLineAt { get; private set; }

    public IrcBot(IrcBotOptions options, ILogger<IrcBot> logger)
    {
        _options = options;
        _logger = logger;
        
        // Built by hand (same constructor the NetIRC builder uses) so the connection can be wrapped.
        IConnection connection = new TcpClientConnection(options.Host, options.Port);
        connection.Disconnected += (_, _) =>
        {
            IsConnected = false;
            lock (_joined) _joined.Clear();
            ChannelsChanged?.Invoke();
        };

        // Every outgoing line goes through this filter, the last point before the socket. It is
        // what guarantees nothing is ever written to a read-only channel, whoever tries to send.
        connection = new OutgoingFilterConnection(connection, line =>
        {
            if (OutgoingFilterConnection.WritesTo(line, options.ReadOnlyChannels))
            {
                logger.LogWarning("Blocked a write to a read-only channel: {Line}", line.Trim());
                return null;
            }

            return options.Username is null ? line : OutgoingFilterConnection.RewriteUsername(line, options.Username);
        });

        if (options.ReadOnlyChannels.Count > 0)
            logger.LogInformation("Read-only channels (never written to): {Channels}", string.Join(", ", options.ReadOnlyChannels));

        _client = new Client(new User(options.Nick, options.RealName), options.Password, connection);
        
        _client.RegistrationCompleted += async (sender, _) =>
        {
            _registeredAt = DateTime.UtcNow;
            IsConnected = true;
            if (sender is Client c)
                await c.SendAsync(new JoinMessage(options.Channel));
        };

        // Track which channels we are in from the server's own JOIN/PART/KICK lines about us.
        _client.IRCMessageParsed += (_, message) =>
        {
            string? channel = message.Parameters.FirstOrDefault();
            if (channel is null)
                return;

            bool changed;
            lock (_joined)
            {
                changed = message.Command switch
                {
                    "JOIN" when IsMe(message.Prefix?.From) => _joined.Add(channel),
                    "PART" when IsMe(message.Prefix?.From) => _joined.Remove(channel),
                    "KICK" when IsMe(message.Parameters.ElementAtOrDefault(1)) => _joined.Remove(channel),
                    _ => false,
                };
            }

            if (changed)
                ChannelsChanged?.Invoke();
        };

        _client.Channels.CollectionChanged += (o1, e) =>
        {
            foreach (Channel ch in e.NewItems ?? Array.Empty<Channel>())
            {
                ch.Messages.CollectionChanged += async (o2, me) =>
                {
                    foreach (ChannelMessage msg in me.NewItems ?? Array.Empty<object>())
                    {
                        if (msg.User.Nick == options.Nick)
                            continue;

                        _logger.LogInformation("[{Channel}] <{Nick}> {Text}", ch.Name, msg.User.Nick, msg.Text);
                        LastLineAt = DateTime.UtcNow;

                        // Every message just goes into the log; the agent reads the log when it
                        // glances. We only hand it a nudge: did the channel change, and was it a
                        // direct mention (the highlight beep)?
                        await LogMessageAsync(ch.Name, msg.User.Nick, msg.Text, isOwn: false);
                        _ = NotifyMessageLogged();   // fire-and-forget: a slow UI subscriber must not stall the IRC loop

                        if (DateTime.UtcNow - _registeredAt < ReplayWindow)
                            continue;

                        bool mentionsMe = msg.Text.Contains(options.Nick, StringComparison.OrdinalIgnoreCase);
                        ChannelActivity?.Invoke(ch.Name, $"<{msg.User.Nick}> {msg.Text}", mentionsMe);
                    }
                };
            }
        };
    }

    /// <summary>True for a channel the bot may read but must never write to (shadow mode).</summary>
    public bool IsReadOnly(string channel) => _options.ReadOnlyChannels.Contains(channel);

    private bool IsMe(string? nick) => string.Equals(nick, _options.Nick, StringComparison.OrdinalIgnoreCase);

    /// <summary>Asks the server to join a channel. <see cref="JoinedChannels"/> updates when it confirms.</summary>
    public Task JoinAsync(string channel) => _client.SendAsync(new JoinMessage(channel));

    /// <summary>Leaves a channel. Behind a bouncer this also stops it rejoining the channel for us.</summary>
    public Task PartAsync(string channel) => _client.SendAsync(new PartMessage(channel));

    /// <summary>
    /// Reads a channel's log written after <paramref name="since"/>, oldest-first, capped to the
    /// most recent <paramref name="maxMessages"/> (a marker replaces older overflow). The last
    /// <paramref name="contextMessages"/> lines from before <paramref name="since"/> are prepended
    /// under their own heading, so the reader can tell who was talking to whom. Empty if nothing is new.
    /// </summary>
    public async Task<string> ReadLogSinceAsync(string channel, DateTime since, int maxMessages, int contextMessages)
    {
        try
        {
            await using var db = new ChattingContext();
            var query = db.Messages.Where(m => m.Channel == channel && m.Timestamp > since);

            int total = await query.CountAsync();
            if (total == 0)
                return "";

            var recent = await query
                .OrderByDescending(m => m.Timestamp)
                .Take(maxMessages)
                .ToListAsync();
            recent.Reverse(); // back to chronological order

            var earlier = await db.Messages
                .Where(m => m.Channel == channel && m.Timestamp <= since)
                .OrderByDescending(m => m.Timestamp)
                .Take(contextMessages)
                .ToListAsync();
            earlier.Reverse();

            var sb = new StringBuilder();
            if (earlier.Count > 0)
            {
                sb.AppendLine("Earlier, already read:");
                foreach (var m in earlier)
                    sb.AppendLine($"<{m.Nick}> {m.Text}");
                sb.AppendLine();
            }

            sb.AppendLine("Since you last looked:");
            if (total > recent.Count)
                sb.AppendLine($"[... {total - recent.Count} earlier messages you missed ...]");
            foreach (var m in recent)
                sb.AppendLine($"<{m.Nick}> {m.Text}");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB error while reading log");
            return "";
        }
    }

    /// <summary>Sends a reply to a channel; returns the logged message's id, or 0 if discarded.</summary>
    public async Task<int> SendMessageAsync(string channel, string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        // The agent doesn't send here in the first place; this and the connection filter are the backstops.
        if (IsReadOnly(channel))
        {
            _logger.LogWarning("Refused to send to read-only channel {Channel}.", channel);
            return 0;
        }

        if (text.Length > MaxReplyLength)
        {
            _logger.LogWarning("Agent error: Reply too long ({TextLength} chars), discarding.", text.Length);
            return 0;
        }

        int charsPerSecond = _rng.Next(4, 8);
        int typingDelay = (text.Length / charsPerSecond) * 1000 + _rng.Next(500, 1500);
        await Task.Delay(typingDelay);

        _logger.LogInformation("[{Channel}] <{Nick}> {Text}", channel, _options.Nick, text);
        await _client.SendAsync(new PrivMsgMessage(channel, text));

        int messageId = await LogMessageAsync(channel, _options.Nick, text, isOwn: true);
        _ = NotifyMessageLogged();   // fire-and-forget: a slow UI subscriber must not stall the code path
        return messageId;
    }

    private async Task NotifyMessageLogged()
    {
        if (MessageLogged == null)
            return;

        try
        {
            await MessageLogged.Invoke();
        }
        catch (Exception ex)
        {
            // A UI subscriber failing must never break the IRC loop.
            _logger.LogError(ex, "MessageLogged subscriber error");
        }
    }

    public Task ConnectAsync() => _client.ConnectAsync();

    /// <summary>Persists a message and returns its new id (0 on failure).</summary>
    private async Task<int> LogMessageAsync(string channel, string nick, string text, bool isOwn)
    {
        try
        {
            await using var db = new ChattingContext();
            var message = new Message { Channel = channel, Nick = nick, Text = text, IsOwnMessage = isOwn };
            db.Messages.Add(message);
            await db.SaveChangesAsync();
            return message.MessageId; // EF populates the PK after SaveChanges
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB error while logging message");
            return 0;
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _client.Dispose();
    }
}

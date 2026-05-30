using System.Text;
using DefinitelyHuman.Data;
using Microsoft.EntityFrameworkCore;
using NetIRC;
using NetIRC.Messages;

namespace DefinitelyHuman.Irc;

public class IrcBot : IDisposable
{
    private readonly Client _client;
    private readonly Random _rng = new();
    
    private readonly IrcBotOptions _options;
    private readonly ILogger<IrcBot> _logger;

    private const int MaxReplyLength = 400;

    /// <summary>
    /// The channel log changed. The string is the new line ("&lt;nick&gt; text", for logging),
    /// and the flag is the highlight "beep": the new line mentions the bot.
    /// </summary>
    public event Action<string, bool>? ChannelActivity;

    /// <summary>Raised after a message is written to the log, so the web dashboard can refresh.</summary>
    public event Func<Task>? MessageLogged;

    public string Channel => _options.Channel;
    public string Nick => _options.Nick;

    public IrcBot(IrcBotOptions options, ILogger<IrcBot> logger)
    {
        _options = options;
        _logger = logger;
        
        var builder = Client.CreateBuilder()
            .WithNick(options.Nick, options.RealName)
            .WithServer(options.Host, options.Port, options.Password);

        _client = builder.Build();
        
        _client.RegistrationCompleted += async (sender, _) =>
        {
            if (sender is Client c)
                await c.SendAsync(new JoinMessage(options.Channel));
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

                        // Every message just goes into the log; the agent reads the log when it
                        // glances. We only hand it a nudge: did the channel change, and was it a
                        // direct mention (the highlight beep)?
                        await LogMessageAsync(ch.Name, msg.User.Nick, msg.Text, isOwn: false);
                        _ = NotifyMessageLogged();   // fire-and-forget: a slow UI subscriber must not stall the IRC loop

                        bool mentionsMe = msg.Text.Contains(options.Nick, StringComparison.OrdinalIgnoreCase);
                        ChannelActivity?.Invoke($"<{msg.User.Nick}> {msg.Text}", mentionsMe);
                    }
                };
            }
        };
    }

    /// <summary>
    /// Reads the channel log written after <paramref name="since"/>, oldest-first, capped to the
    /// most recent <paramref name="maxMessages"/> (a marker replaces older overflow).
    /// </summary>
    public async Task<string> ReadLogSinceAsync(DateTime since, int maxMessages)
    {
        try
        {
            await using var db = new ChattingContext();
            var query = db.Messages.Where(m => m.Channel == _options.Channel && m.Timestamp > since);

            int total = await query.CountAsync();
            if (total == 0)
                return "";

            var recent = await query
                .OrderByDescending(m => m.Timestamp)
                .Take(maxMessages)
                .ToListAsync();
            recent.Reverse(); // back to chronological order

            var sb = new StringBuilder();
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

    public Task SendMessageAsync(string text) => SendMessageAsync(_options.Channel, text);

    private async Task SendMessageAsync(string channel, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (text.Length > MaxReplyLength)
        {
            _logger.LogWarning("Agent error: Reply too long ({TextLength} chars), discarding.", text.Length);
            return;
        }

        int charsPerSecond = _rng.Next(4, 8);
        int typingDelay = (text.Length / charsPerSecond) * 1000 + _rng.Next(500, 1500);
        await Task.Delay(typingDelay);
        
        _logger.LogInformation("[{Channel}] <{Nick}> {Text}", channel, _options.Nick, text);
        await _client.SendAsync(new PrivMsgMessage(channel, text));

        await LogMessageAsync(channel, _options.Nick, text, isOwn: true);
        _ = NotifyMessageLogged();   // fire-and-forget: a slow UI subscriber must not stall the code path
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

    private async Task LogMessageAsync(string channel, string nick, string text, bool isOwn)
    {
        try
        {
            await using var db = new ChattingContext();
            db.Messages.Add(new Message { Channel = channel, Nick = nick, Text = text, IsOwnMessage = isOwn });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB error while logging message");
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _client.Dispose();
    }
}

using NetIRC.Connection;

namespace DefinitelyHuman.Irc;

/// <summary>
/// Wraps NetIRC's connection so every outgoing line passes through one filter, which can rewrite
/// it or drop it (by returning null). This is the last point before the socket, so a rule
/// enforced here holds no matter which code tried to send.
/// </summary>
public sealed class OutgoingFilterConnection(IConnection inner, Func<string, string?> filter) : IConnection
{
    public event EventHandler<DataReceivedEventArgs> DataReceived
    {
        add => inner.DataReceived += value;
        remove => inner.DataReceived -= value;
    }

    public event EventHandler Connected
    {
        add => inner.Connected += value;
        remove => inner.Connected -= value;
    }

    public event EventHandler Disconnected
    {
        add => inner.Disconnected += value;
        remove => inner.Disconnected -= value;
    }

    public Task ConnectAsync() => inner.ConnectAsync();

    public Task SendAsync(string data) => filter(data) is { } line ? inner.SendAsync(line) : Task.CompletedTask;

    public void Dispose() => inner.Dispose();

    /// <summary>
    /// NetIRC always registers with the nick as the IRC username. Bouncers like soju pick the
    /// network from the username ("user/network"), so this swaps it in the outgoing USER line:
    /// "USER nick 0 - :real" becomes "USER username 0 - :real"; anything else passes through.
    /// </summary>
    public static string RewriteUsername(string data, string username)
    {
        if (!data.StartsWith("USER ", StringComparison.Ordinal))
            return data;

        int rest = data.IndexOf(' ', 5);
        return rest < 0 ? data : $"USER {username}{data[rest..]}";
    }

    // Commands that say or change something in a channel. JOIN, PART and queries are not here.
    private static readonly HashSet<string> WritingCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "PRIVMSG", "NOTICE", "TAGMSG", "TOPIC", "KICK", "MODE", "INVITE",
    };

    /// <summary>
    /// True if any line in <paramref name="data"/> would write to one of <paramref name="channels"/>:
    /// a writing command whose target list (or, for INVITE, second parameter) names one of them.
    /// </summary>
    public static bool WritesTo(string data, IReadOnlySet<string> channels)
    {
        if (channels.Count == 0)
            return false;

        foreach (string line in data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Parameters before the trailing ":text"; a leading "@tags" or ":prefix" is skipped.
            var words = line.Split(" :", 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .SkipWhile(w => w[0] is '@' or ':').ToArray();
            if (words.Length < 2 || !WritingCommands.Contains(words[0]))
                continue;

            if (words.Skip(1).Take(2).SelectMany(w => w.Split(',')).Any(channels.Contains))
                return true;
        }

        return false;
    }
}

namespace DefinitelyHuman.Irc;

public struct IrcBotOptions
{
    public string Nick { get; set; }
    
    public string RealName
    {
        get => string.IsNullOrWhiteSpace(field) ? Nick : field; 
        set => field = value.Trim();
    }
    
    public string Host { get; set; }
    
    public int Port { get; set; }
    
    public string Channel { get; set; }
    
    public string? Password
    {
        get => field;
        set => field = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>IRC username sent at registration; blank means NetIRC's default (the nick).</summary>
    public string? Username
    {
        get => field;
        set => field = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Channels the bot may sit in and read but must never write to. There it runs in shadow
    /// mode: it decides as usual, and what it would have said is only recorded.
    /// </summary>
    public IReadOnlySet<string> ReadOnlyChannels
    {
        get => field ?? new HashSet<string>();
        set;
    }

    /// <summary>Parses a comma- or space-separated channel list, e.g. "#gamedev, #other".</summary>
    public static IReadOnlySet<string> ParseChannels(string? list) =>
        new HashSet<string>(
            (list ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
}
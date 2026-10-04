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
}
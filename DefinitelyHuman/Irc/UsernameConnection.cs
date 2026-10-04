using NetIRC.Connection;

namespace DefinitelyHuman.Irc;

/// <summary>
/// NetIRC always registers with the nick as the IRC username. Bouncers like soju pick the
/// network from the username ("user/network"), so this swaps it in the outgoing USER line.
/// </summary>
public sealed class UsernameConnection(IConnection inner, string username) : IConnection
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

    public Task SendAsync(string data) => inner.SendAsync(Rewrite(data, username));

    public void Dispose() => inner.Dispose();

    /// <summary>"USER nick 0 - :real" becomes "USER username 0 - :real"; anything else passes through.</summary>
    public static string Rewrite(string data, string username)
    {
        if (!data.StartsWith("USER ", StringComparison.Ordinal))
            return data;

        int rest = data.IndexOf(' ', 5);
        return rest < 0 ? data : $"USER {username}{data[rest..]}";
    }
}

using System.Net;
using System.Net.Sockets;
using IrcBouncer.Config;
using IrcBouncer.Utilities;

namespace IrcBouncer;

public sealed class Bouncer(BouncerConfig config)
{
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _upstreamWriteLock = new(1, 1);
    private readonly RingBuffer _buffer = new(config.BufferSize);
    private readonly TaskCompletionSource _credentialsReady = new();

    private IrcConnection? _upstream;
    private IrcConnection? _bot;
    private bool _upstreamReady;
    private int _backoffSeconds = 5;

    // Learned from the bot's first connection
    private string? _nick;
    private string? _password;
    private string? _channel;

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, config.ListenPort);
        listener.Start();
        Log($"Listening on localhost:{config.ListenPort}");

        var upstreamTask = UpstreamLoopAsync(ct);
        var acceptTask = BotAcceptLoopAsync(listener, ct);

        try
        {
            await Task.WhenAny(upstreamTask, acceptTask);
        }
        finally
        {
            listener.Stop();
            _bot?.Dispose();
            if (_upstream is not null)
            {
                try { await _upstream.WriteLineAsync("QUIT :Bouncer shutting down"); } catch { /* ignored */ }
                _upstream.Dispose();
            }
        }

        await Task.WhenAll(upstreamTask, acceptTask);
    }

    private async Task UpstreamLoopAsync(CancellationToken ct)
    {
        // Wait for the first bot to provide credentials before connecting upstream
        await Task.WhenAny(_credentialsReady.Task, Task.Delay(Timeout.Infinite, ct));
        ct.ThrowIfCancellationRequested();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectUpstreamAsync(ct);
                _backoffSeconds = 5;
                await UpstreamReadLoopAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log($"Upstream error: {ex.Message}");
                _upstreamReady = false;
                _upstream?.Dispose();
                _upstream = null;
            }

            if (ct.IsCancellationRequested) return;

            Log($"Reconnecting in {_backoffSeconds}s...");
            try { await Task.Delay(TimeSpan.FromSeconds(_backoffSeconds), ct); }
            catch (OperationCanceledException) { return; }

            _backoffSeconds = Math.Min(_backoffSeconds * 2, 300);
        }
    }

    private async Task ConnectUpstreamAsync(CancellationToken ct)
    {
        Log($"Connecting to {config.UpstreamHost}:{config.UpstreamPort} as {_nick}...");
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(config.UpstreamHost, config.UpstreamPort, ct);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        var conn = new IrcConnection(tcp);
        _upstream = conn;

        if (_password is not null)
            await conn.WriteLineAsync($"PASS {_password}");
        await conn.WriteLineAsync($"NICK {_nick}");
        await conn.WriteLineAsync($"USER {_nick} 0 * :{_nick}");

        using var regTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        regTimeout.CancelAfter(TimeSpan.FromSeconds(60));

        while (true)
        {
            string? line = await conn.ReadLineAsync(regTimeout.Token);
            if (line is null) throw new IOException("Upstream closed during registration");

            if (line.StartsWith("PING"))
            {
                string token = line.Length > 5 ? line[5..] : "";
                await conn.WriteLineAsync($"PONG {token}");
                continue;
            }

            string[] parts = line.Split(' ', 4);
            switch (parts)
            {
                case [_, "001", ..]:
                {
                    _upstreamReady = true;
                    Log("Upstream registered successfully.");

                    // Re-join channel if we had one (upstream reconnect scenario)
                    if (_channel is not null)
                        await conn.WriteLineAsync($"JOIN {_channel}");

                    return;
                }
                case [_, "433", ..] or [_, "464", ..]:
                    throw new InvalidOperationException($"Registration rejected: {line}");
            }
        }
    }

    private async Task UpstreamReadLoopAsync(CancellationToken ct)
    {
        var conn = _upstream!;
        while (!ct.IsCancellationRequested)
        {
            string? line = await conn.ReadLineAsync(ct);
            if (line is null)
                throw new IOException("Upstream connection closed.");

            if (line.StartsWith("PING"))
            {
                string token = line.Length > 5 ? line[5..] : "";
                await _upstreamWriteLock.WaitAsync(ct);
                try { await conn.WriteLineAsync($"PONG {token}"); }
                finally { _upstreamWriteLock.Release(); }
                continue;
            }

            // Track server-initiated nick changes
            if (line.Length > 1 && line[0] == ':')
            {
                int sp = line.IndexOf(' ');
                if (sp > 0)
                {
                    string prefix = line[1..sp];
                    var rest = line.AsSpan(sp + 1);
                    if (rest.StartsWith("NICK", StringComparison.OrdinalIgnoreCase))
                    {
                        string fromNick = prefix.Split('!')[0];
                        if (fromNick.Equals(_nick, StringComparison.OrdinalIgnoreCase))
                        {
                            string[] nickParts = line.Split(' ', 3);
                            if (nickParts.Length >= 3)
                                _nick = nickParts[2].TrimStart(':');
                        }
                    }

                }
            }

            bool isChannelPrivmsg = _channel is not null && IsPrivmsgTo(line, _channel);
            bool isKick = _channel is not null && IsKickFrom(line, _channel, _nick);

            await _stateLock.WaitAsync(ct);
            try
            {
                if (isKick)
                {
                    Log($"Kicked from {_channel}.");
                    _channel = null;
                    _buffer.Clear();
                }
                else if (_bot is not null)
                {
                    await _bot.WriteLineAsync(line);
                }
                else if (isChannelPrivmsg)
                {
                    _buffer.Add(line);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { _stateLock.Release(); }
        }
    }

    private async Task BotAcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }

            Log("Bot connected.");

            await _stateLock.WaitAsync(ct);
            try
            {
                _bot?.Dispose();
                _bot = null;
            }
            finally { _stateLock.Release(); }

            _ = HandleBotAsync(tcp, ct);
        }
    }

    private async Task HandleBotAsync(TcpClient tcp, CancellationToken ct)
    {
        var conn = new IrcConnection(tcp);
        try
        {
            await BotHandshakeAsync(conn, ct);
            await BotReadLoopAsync(conn, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log($"Bot disconnected: {ex.Message}");
        }
        finally
        {
            // ReSharper disable once MethodSupportsCancellation
            await _stateLock.WaitAsync();
            try
            {
                if (_bot == conn)
                    _bot = null;
            }
            finally { _stateLock.Release(); }
            conn.Dispose();
        }
    }

    private async Task BotHandshakeAsync(IrcConnection conn, CancellationToken ct)
    {
        string? botNick = null;
        string? botPass = null;
        var gotUser = false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        while (!gotUser)
        {
            string? line = await conn.ReadLineAsync(timeout.Token);
            if (line is null) throw new IOException("Bot closed during handshake");

            if (line.StartsWith("PASS", StringComparison.OrdinalIgnoreCase))
            {
                botPass = line.Length > 5 ? line[5..].Trim() : null;
                continue;
            }
            if (line.StartsWith("NICK", StringComparison.OrdinalIgnoreCase))
            {
                botNick = line.Length > 5 ? line[5..].Trim() : null;
                continue;
            }
            if (line.StartsWith("USER", StringComparison.OrdinalIgnoreCase))
                gotUser = true;
        }

        // On first connection, learn identity and start upstream
        if (_nick is null)
        {
            _nick = botNick ?? "bouncer";
            _password = string.IsNullOrWhiteSpace(botPass) ? null : botPass;
            _credentialsReady.TrySetResult();
            Log($"Learned identity: nick={_nick}");
        }

        // Wait for upstream to be ready
        using var upstreamWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        upstreamWait.CancelAfter(TimeSpan.FromSeconds(30));
        while (!_upstreamReady)
            await Task.Delay(100, upstreamWait.Token);

        // Send synthetic registration to bot
        const string srv = "bouncer.local";
        string nick = _nick!;
        await conn.WriteLineAsync($":{srv} 001 {nick} :Welcome to the IRC Bouncer");
        await conn.WriteLineAsync($":{srv} 002 {nick} :Your host is {srv}");
        await conn.WriteLineAsync($":{srv} 003 {nick} :This server was created now");
        await conn.WriteLineAsync($":{srv} 004 {nick} {srv} irc-bouncer o o");
        await conn.WriteLineAsync($":{srv} 005 {nick} NETWORK=Bouncer :are supported by this server");
        await conn.WriteLineAsync($":{srv} 376 {nick} :End of /MOTD command.");

        // Wait for JOIN from bot
        using var joinTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        joinTimeout.CancelAfter(TimeSpan.FromSeconds(10));

        while (true)
        {
            string? line = await conn.ReadLineAsync(joinTimeout.Token);
            if (line is null) throw new IOException("Bot closed before JOIN");
            if (line.StartsWith("JOIN", StringComparison.OrdinalIgnoreCase))
            {
                string requestedChannel = line.Length > 5 ? line[5..].Trim() : "";
                await HandleJoinFromBot(conn, nick, srv, requestedChannel, ct);
                break;
            }
        }
    }

    private async Task HandleJoinFromBot(IrcConnection conn, string nick, string srv, string requestedChannel, CancellationToken ct)
    {
        if (_channel is null)
        {
            // First time joining — forward to upstream
            _channel = requestedChannel;
            Log($"Joining {_channel} on upstream.");

            await _upstreamWriteLock.WaitAsync(ct);
            try { await _upstream!.WriteLineAsync($"JOIN {_channel}"); }
            finally { _upstreamWriteLock.Release(); }

            await conn.WriteLineAsync($":{nick}!{nick}@bouncer.local JOIN {_channel}");
            await conn.WriteLineAsync($":{srv} 353 {nick} = {_channel} :{nick}");
            await conn.WriteLineAsync($":{srv} 366 {nick} {_channel} :End of /NAMES list.");

            await _stateLock.WaitAsync(ct);
            try { _bot = conn; }
            finally { _stateLock.Release(); }

            Log("Bot handshake complete (first connection).");
        }
        else if (_channel.Equals(requestedChannel, StringComparison.OrdinalIgnoreCase))
        {
            // Reconnect — same channel, replay buffer
            await conn.WriteLineAsync($":{nick}!{nick}@bouncer.local JOIN {_channel}");
            await conn.WriteLineAsync($":{srv} 353 {nick} = {_channel} :{nick}");
            await conn.WriteLineAsync($":{srv} 366 {nick} {_channel} :End of /NAMES list.");

            await _stateLock.WaitAsync(ct);
            int replayed;
            try
            {
                string[] buffered = _buffer.Drain();
                replayed = buffered.Length;
                foreach (string msg in buffered)
                    await conn.WriteLineAsync(msg);
                _bot = conn;
            }
            finally { _stateLock.Release(); }

            Log($"Bot handshake complete. Replayed {replayed} buffered messages.");
        }
        else
        {
            // Different channel — reject (must PART first)
            await conn.WriteLineAsync($":{srv} 405 {nick} {requestedChannel} :You have joined too many channels");

            // Still activate bot so it can send PART
            await _stateLock.WaitAsync(ct);
            try { _bot = conn; }
            finally { _stateLock.Release(); }

            Log($"Bot tried to join {requestedChannel} but is in {_channel}. Sent 405.");
        }
    }

    private async Task BotReadLoopAsync(IrcConnection conn, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line = await conn.ReadLineAsync(ct);
            if (line is null)
                throw new IOException("Bot connection closed.");

            if (line.StartsWith("PING", StringComparison.OrdinalIgnoreCase))
            {
                string token = line.Length > 5 ? line[5..] : "";
                await conn.WriteLineAsync($"PONG {token}");
                continue;
            }

            if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
            {
                Log("Bot sent QUIT — disconnecting bot, keeping upstream alive.");
                return;
            }

            // Registration commands — always absorb
            if (line.StartsWith("USER", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("PASS", StringComparison.OrdinalIgnoreCase))
                continue;

            // NICK change — forward to upstream
            if (line.StartsWith("NICK", StringComparison.OrdinalIgnoreCase))
            {
                var upstream = _upstream;
                if (upstream is not null && _upstreamReady)
                {
                    await _upstreamWriteLock.WaitAsync(ct);
                    try { await upstream.WriteLineAsync(line); }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                    finally { _upstreamWriteLock.Release(); }

                    string newNick = line.Length > 5 ? line[5..].Trim().TrimStart(':') : "";
                    if (!string.IsNullOrEmpty(newNick))
                        _nick = newNick;
                }
                continue;
            }

            // JOIN — handle channel logic
            if (line.StartsWith("JOIN", StringComparison.OrdinalIgnoreCase))
            {
                string requestedChannel = line.Length > 5 ? line[5..].Trim() : "";
                await HandleLiveJoin(conn, requestedChannel, ct);
                continue;
            }

            // PART — leave channel
            if (line.StartsWith("PART", StringComparison.OrdinalIgnoreCase))
            {
                string partChannel = line.Length > 5 ? line[5..].Trim().Split(' ')[0] : "";
                if (_channel is not null && _channel.Equals(partChannel, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"Bot parted {_channel}.");
                    var upstream = _upstream;
                    if (upstream is not null && _upstreamReady)
                    {
                        await _upstreamWriteLock.WaitAsync(ct);
                        try { await upstream.WriteLineAsync($"PART {_channel}"); }
                        catch (IOException) { }
                        catch (ObjectDisposedException) { }
                        finally { _upstreamWriteLock.Release(); }
                    }
                    _channel = null;
                    _buffer.Clear();
                }
                continue;
            }

            // Forward everything else (PRIVMSG, etc.) to upstream
            var upstreamConn = _upstream;
            if (upstreamConn is not null && _upstreamReady)
            {
                await _upstreamWriteLock.WaitAsync(ct);
                try { await upstreamConn.WriteLineAsync(line); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                finally { _upstreamWriteLock.Release(); }
            }
        }
    }

    private async Task HandleLiveJoin(IrcConnection conn, string requestedChannel, CancellationToken ct)
    {
        const string srv = "bouncer.local";
        string nick = _nick ?? "user";

        if (_channel is null)
        {
            // No channel — forward to upstream
            _channel = requestedChannel;
            Log($"Joining {_channel} on upstream.");

            var upstream = _upstream;
            if (upstream is not null && _upstreamReady)
            {
                await _upstreamWriteLock.WaitAsync(ct);
                try { await upstream.WriteLineAsync($"JOIN {_channel}"); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                finally { _upstreamWriteLock.Release(); }
            }

            await conn.WriteLineAsync($":{nick}!{nick}@bouncer.local JOIN {_channel}");
            await conn.WriteLineAsync($":{srv} 353 {nick} = {_channel} :{nick}");
            await conn.WriteLineAsync($":{srv} 366 {nick} {_channel} :End of /NAMES list.");
        }
        else if (_channel.Equals(requestedChannel, StringComparison.OrdinalIgnoreCase))
        {
            // Already in this channel — swallow
        }
        else
        {
            // Different channel — reject
            await conn.WriteLineAsync($":{srv} 405 {nick} {requestedChannel} :You have joined too many channels");
        }
    }

    // :prefix KICK #channel nick :reason
    private static bool IsKickFrom(string line, string channel, string? nick)
    {
        if (nick is null || !line.StartsWith(':')) return false;
        string[] parts = line.Split(' ', 5);
        return parts.Length >= 4
            && parts[1].Equals("KICK", StringComparison.OrdinalIgnoreCase)
            && parts[2].Equals(channel, StringComparison.OrdinalIgnoreCase)
            && parts[3].Equals(nick, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivmsgTo(string line, string channel)
    {
        if (!line.StartsWith(':')) return false;
        int firstSpace = line.IndexOf(' ');
        if (firstSpace < 0) return false;
        var rest = line.AsSpan(firstSpace + 1);
        if (!rest.StartsWith("PRIVMSG ", StringComparison.OrdinalIgnoreCase)) return false;
        var afterCmd = rest[8..];
        int targetEnd = afterCmd.IndexOf(' ');
        var target = targetEnd > 0 ? afterCmd[..targetEnd] : afterCmd;
        return target.Equals(channel.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
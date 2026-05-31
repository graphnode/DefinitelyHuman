using System.Net.Sockets;
using System.Text;

namespace IrcBouncer;

internal sealed class IrcConnection : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    public IrcConnection(TcpClient tcp)
    {
        _tcp = tcp;
        var stream = tcp.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true, NewLine = "\r\n" };
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _reader.ReadLineAsync(ct);
    }

    public Task WriteLineAsync(string line) => _writer.WriteLineAsync(line);

    public void Dispose()
    {
        try { _writer.Dispose(); } catch { /* ignored */ }
        try { _reader.Dispose(); } catch { /* ignored */ }
        try { _tcp.Dispose(); } catch { /* ignored */ }
    }
}
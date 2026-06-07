using System.Net.Sockets;

namespace McpChannelVoice.Services.WyomingProtocol;

public sealed class WyomingClient : IAsyncDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private WyomingWriter? _writer;
    private WyomingReader? _reader;

    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port, ct);
        _stream = _tcp.GetStream();
        _writer = new WyomingWriter(_stream);
        // Buffer reads so the per-chunk JSONL header isn't scanned one syscall per byte off the raw
        // socket. The writer keeps the unbuffered stream (sockets are full-duplex; the reader only reads).
        _reader = new WyomingReader(new BufferedStream(_stream));
    }

    public Task WriteAsync(WyomingEvent evt, CancellationToken ct) =>
        (_writer ?? throw new InvalidOperationException("Not connected")).WriteAsync(evt, ct);

    public IAsyncEnumerable<WyomingEvent> ReadAllAsync(CancellationToken ct) =>
        (_reader ?? throw new InvalidOperationException("Not connected")).ReadAllAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync();
        }
        _tcp?.Dispose();
    }
}
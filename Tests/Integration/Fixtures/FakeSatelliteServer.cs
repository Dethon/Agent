using System.Net;
using System.Net.Sockets;
using McpChannelVoice.Services.WyomingProtocol;

namespace Tests.Integration.Fixtures;

// Minimal in-process Wyoming satellite: accepts ONE hub connection, records every event the hub
// sends, and lets the test push satellite->hub events. Mirrors nabu-satellite's wire behavior only
// as far as arbitration needs — a turn is announced with run-pipeline (which alone carries
// {source, wake_rms, wake_score}) and the mic stream follows as bare audio-chunks. The real
// satellite never sends audio-start to the hub, so neither does this: driving a frame production
// does not send would exercise a path production does not take.
public sealed class FakeSatelliteServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<WyomingEvent> _received = [];
    private readonly Lock _gate = new();
    private TcpClient? _connection;
    private NetworkStream? _stream;
    private WyomingWriter? _writer;
    private Task? _pump;

    public FakeSatelliteServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string Address => $"tcp://127.0.0.1:{Port}";

    // Snapshot copy: the read pump appends on its own task while assertions enumerate.
    public IReadOnlyList<WyomingEvent> ReceivedEvents
    {
        get
        {
            lock (_gate)
            {
                return _received.ToArray();
            }
        }
    }

    public async Task AcceptAsync(CancellationToken ct = default)
    {
        _connection = await _listener.AcceptTcpClientAsync(ct);
        _stream = _connection.GetStream();
        _writer = new WyomingWriter(_stream);
        var reader = new WyomingReader(new BufferedStream(_stream));
        _pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in reader.ReadAllAsync(_cts.Token))
                {
                    lock (_gate)
                    {
                        _received.Add(evt);
                    }
                }
            }
            catch
            {
                // The socket is torn down with the test; a read fault here is the teardown, not a
                // finding — the assertions run against whatever was recorded before it.
            }
        }, CancellationToken.None);
    }

    public Task SendAsync(WyomingEvent evt) =>
        (_writer ?? throw new InvalidOperationException("AcceptAsync has not completed"))
            .WriteAsync(evt, _cts.Token);

    public int CountOf(string type) => ReceivedEvents.Count(e => e.Type == type);

    // Polling rather than per-type signalling keeps the pump a plain recorder: every assertion in
    // an arbitration test is "did this frame reach this satellite", and a poll answers that without
    // the test having to declare up front which frames it will wait for.
    public async Task<WyomingEvent> WaitForEventAsync(string type, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            var events = ReceivedEvents;
            if (events.FirstOrDefault(e => e.Type == type) is { } match)
            {
                return match;
            }
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException(
                    $"Satellite on port {Port} never received a '{type}' frame within {timeout}. " +
                    $"Saw: [{string.Join(", ", events.Select(e => e.Type))}]");
            }
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        if (_stream is not null)
        {
            await _stream.DisposeAsync();
        }
        _connection?.Dispose();
        if (_pump is not null)
        {
            try
            {
                await _pump;
            }
            catch
            {
                // Unwinds on cancellation / socket disposal.
            }
        }
        _cts.Dispose();
    }
}
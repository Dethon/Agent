using System.Threading.Channels;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed class SatelliteSession
{
    private readonly Channel<ReadOnlyMemory<byte>> _inbound = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

    public SatelliteSession(string satelliteId, SatelliteConfig config)
    {
        SatelliteId = satelliteId;
        Config = config;
    }

    public string SatelliteId { get; }
    public string ConversationId => SatelliteId;
    public SatelliteConfig Config { get; }

    public ValueTask PublishAudioAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct) =>
        _inbound.Writer.WriteAsync(bytes, ct);

    public void CompleteInboundAudio() => _inbound.Writer.TryComplete();

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadInboundAudioAsync(CancellationToken ct) =>
        _inbound.Reader.ReadAllAsync(ct);
}
using System.Threading.Channels;
using Domain.DTOs;

namespace Domain.Memory;

public sealed class MemoryExtractionQueue
{
    private readonly Channel<MemoryExtractionRequest> _channel =
        Channel.CreateUnbounded<MemoryExtractionRequest>(
            new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(MemoryExtractionRequest request, CancellationToken ct) =>
        _channel.Writer.WriteAsync(request, ct);

    public IAsyncEnumerable<MemoryExtractionRequest> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    public void Complete() => _channel.Writer.Complete();
}

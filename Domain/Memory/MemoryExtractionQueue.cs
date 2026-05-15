using System.Threading.Channels;
using Domain.DTOs;

namespace Domain.Memory;

public sealed class MemoryExtractionQueue
{
    private readonly Channel<MemoryExtractionRequest> _channel;

    public MemoryExtractionQueue(int capacity = 512)
    {
        _channel = Channel.CreateBounded<MemoryExtractionRequest>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });
    }

    // DropOldest makes TryWrite always succeed, so enqueue never blocks the agent turn.
    public ValueTask EnqueueAsync(MemoryExtractionRequest request, CancellationToken ct)
    {
        _channel.Writer.TryWrite(request);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<MemoryExtractionRequest> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    public void Complete() => _channel.Writer.Complete();
}
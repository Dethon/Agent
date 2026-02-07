using System.Threading.Channels;

namespace Infrastructure.Clients.Messaging.WebChat;

public sealed class BroadcastChannel<T>
{
    private readonly List<Channel<T>> _subscribers = [];
    private readonly Lock _lock = new();

    public ChannelReader<T> Subscribe()
    {
        var channel = Channel.CreateUnbounded<T>();
        lock (_lock)
        {
            _subscribers.Add(channel);
        }

        return channel.Reader;
    }

    public async Task WriteAsync(T item, CancellationToken cancellationToken)
    {
        List<Channel<T>> subs;
        lock (_lock)
        {
            subs = [.. _subscribers];
        }

        await Task.WhenAll(subs.Select(s => s.Writer.WriteAsync(item, cancellationToken).AsTask()));
    }

    public void Complete()
    {
        lock (_lock)
        {
            foreach (var s in _subscribers)
            {
                s.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }
}
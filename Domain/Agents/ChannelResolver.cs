using System.Collections.Concurrent;
using System.Threading.Channels;
using Domain.DTOs;

namespace Domain.Agents;

public class ChannelResolver
{
    private readonly ConcurrentDictionary<AgentKey, Channel<ChatPrompt>> _cache = [];
    private readonly Lock _lock = new();

    public IEnumerable<AgentKey> AgentKeys => _cache.Keys;

    public (Channel<ChatPrompt> channel, bool isNew) Resolve(AgentKey key)
    {
        lock (_lock)
        {
            var channel = _cache.GetValueOrDefault(key);
            if (channel is not null)
            {
                return (channel, false);
            }

            channel = Channel.CreateBounded<ChatPrompt>(new BoundedChannelOptions(500)
            {
                FullMode = BoundedChannelFullMode.Wait
            });
            _cache[key] = channel;
            return (channel, true);
        }
    }

    public void Clean(AgentKey key)
    {
        _cache.Remove(key, out var channel);
        channel?.Writer.TryComplete();
    }
}
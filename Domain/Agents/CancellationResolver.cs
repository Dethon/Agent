using System.Collections.Concurrent;

namespace Domain.Agents;

public class CancellationResolver
{
    private readonly ConcurrentDictionary<AgentKey, CancellationTokenSource> _sources = [];
    private readonly Lock _lock = new();

    public CancellationTokenSource GetOrCreate(AgentKey key)
    {
        lock (_lock)
        {
            if (_sources.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var cts = new CancellationTokenSource();
            _sources[key] = cts;
            return cts;
        }
    }

    public async Task CancelAndRemove(AgentKey key)
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            _sources.Remove(key, out cts);
        }

        if (cts is not null)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }
    }

    public void Clean(long chatId, long threadId)
    {
        var key = new AgentKey(chatId, threadId);
        lock (_lock)
        {
            if (_sources.Remove(key, out var cts))
            {
                cts.Dispose();
            }
        }
    }
}
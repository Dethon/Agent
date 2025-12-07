using System.Collections.Concurrent;

namespace Domain.Agents;

public class CancellationResolver
{
    private readonly ConcurrentDictionary<AgentKey, CancellationTokenSource> _cache = [];
    private readonly Lock _lock = new();

    public IEnumerable<AgentKey> AgentKeys => _cache.Keys;

    public CancellationTokenSource Resolve(AgentKey key)
    {
        lock (_lock)
        {
            var cts = _cache.GetValueOrDefault(key);
            if (cts is not null)
            {
                return cts;
            }

            cts = new CancellationTokenSource();
            _cache[key] = cts;
            return cts;
        }
    }

    public CancellationTokenSource GetLinkedTokenSource(AgentKey key, CancellationToken externalToken)
    {
        var cts = Resolve(key);
        return CancellationTokenSource.CreateLinkedTokenSource(cts.Token, externalToken);
    }

    public void Clean(AgentKey key)
    {
        lock (_lock)
        {
            if (_cache.Remove(key, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
    }
}
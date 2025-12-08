using System.Collections.Concurrent;

namespace Domain.Agents;

public class CancellationResolver
{
    private readonly ConcurrentDictionary<AgentKey, Lazy<CancellationTokenSource>> _cache = [];

    public IEnumerable<AgentKey> AgentKeys => _cache.Keys;

    public CancellationTokenSource Resolve(AgentKey key)
    {
        return _cache.GetOrAdd(key, _ => new Lazy<CancellationTokenSource>(() => new CancellationTokenSource())).Value;
    }

    public CancellationTokenSource GetLinkedTokenSource(AgentKey key, CancellationToken externalToken)
    {
        var cts = Resolve(key);
        return CancellationTokenSource.CreateLinkedTokenSource(cts.Token, externalToken);
    }

    public void Clean(AgentKey key)
    {
        if (!_cache.Remove(key, out var lazy) || !lazy.IsValueCreated)
        {
            return;
        }

        lazy.Value.Cancel();
        lazy.Value.Dispose();
    }
}
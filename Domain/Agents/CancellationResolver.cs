using System.Collections.Concurrent;

namespace Domain.Agents;

public class CancellationResolver
{
    private readonly ConcurrentDictionary<AgentKey, CancellationTokenSource> _sources = [];
    private readonly Lock _lock = new();

    public CancellationTokenSource Resolve(AgentKey key)
    {
        lock (_lock)
        {
            var cts = _sources.GetValueOrDefault(key);
            if (cts is not null)
            {
                return cts;
            }

            cts = new CancellationTokenSource();
            _sources[key] = cts;
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
            if (_sources.Remove(key, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
    }
}
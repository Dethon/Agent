using System.Collections.Concurrent;
using ModelContextProtocol.Server;

namespace McpServer.Download.ResourceSubscriptions;

public class SubscriptionTracker
{
    private readonly Lock _cacheLock = new();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, IMcpServer>>
        _subscribedResources = new();

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IMcpServer>> Get()
    {
        lock (_cacheLock)
        {
            return _subscribedResources.ToDictionary(
                kvp => kvp.Key,
                IReadOnlyDictionary<string, IMcpServer> (kvp) => kvp.Value.ToDictionary());
        }
    }

    public void Add(string sessionId, string uri, IMcpServer server)
    {
        lock (_cacheLock)
        {
            _subscribedResources
                .GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, IMcpServer>())
                .TryAdd(uri, server);
        }
    }

    public void Remove(string sessionId, string uri)
    {
        lock (_cacheLock)
        {
            var dict = _subscribedResources.GetValueOrDefault(sessionId);
            dict?.Remove(uri, out _);
        }
    }
}
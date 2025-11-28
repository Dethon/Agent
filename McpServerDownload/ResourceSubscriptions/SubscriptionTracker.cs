using System.Collections.Concurrent;
using ModelContextProtocol.Server;

namespace McpServerDownload.ResourceSubscriptions;

public class SubscriptionTracker
{
    private readonly Lock _cacheLock = new();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, McpServer>>
        _subscribedResources = new();

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, McpServer>> Get()
    {
        lock (_cacheLock)
        {
            return _subscribedResources.ToDictionary(
                kvp => kvp.Key,
                IReadOnlyDictionary<string, McpServer> (kvp) => kvp.Value.ToDictionary());
        }
    }

    public void Add(string sessionId, string uri, McpServer server)
    {
        lock (_cacheLock)
        {
            _subscribedResources
                .GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, McpServer>())
                .TryAdd(uri, server);
        }
    }

    public void Remove(string sessionId, string uri)
    {
        lock (_cacheLock)
        {
            var dict = _subscribedResources.GetValueOrDefault(sessionId);
            if (dict is null)
            {
                return;
            }

            dict.Remove(uri, out _);
            if (dict.IsEmpty)
            {
                _subscribedResources.Remove(sessionId, out _);
            }
        }
    }
}
using System.Collections.Concurrent;
using ModelContextProtocol.Server;

namespace McpServerLibrary.ResourceSubscriptions;

public class SubscriptionTracker
{
    private readonly Lock _cacheLock = new();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, McpServer>>
        _subscribedResources = [];

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

    public void RemoveSession(string sessionId)
    {
        // Lock is not needed here as ConcurrentDictionary handles thread safety.
        // In other methods, we lock to ensure atomic operations across multiple steps.
        // ReSharper disable once InconsistentlySynchronizedField
        _subscribedResources.Remove(sessionId, out _);
    }
}
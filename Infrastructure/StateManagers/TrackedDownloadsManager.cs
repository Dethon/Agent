using System.Collections.Concurrent;
using Domain.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace Infrastructure.StateManagers;

public class TrackedDownloadsManager(IMemoryCache cache) : ITrackedDownloadsManager
{
    private readonly Lock _cacheLock = new();

    private static string GetTrackedDownloadsKey(string sessionId)
    {
        return $"TrackedDownloads_{sessionId}";
    }

    public int[]? Get(string sessionId)
    {
        return cache
            .Get<ConcurrentDictionary<int, byte>>(GetTrackedDownloadsKey(sessionId))?.Keys
            .Order()
            .ToArray();
    }

    public void Add(string sessionId, int downloadId)
    {
        lock (_cacheLock)
        {
            var dict = cache.GetOrCreate(GetTrackedDownloadsKey(sessionId), entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(60);
                return new ConcurrentDictionary<int, byte>();
            });
            dict?.TryAdd(downloadId, 1);
        }
    }

    public void Remove(string sessionId, int downloadId)
    {
        var dict = cache.Get<ConcurrentDictionary<int, byte>>(GetTrackedDownloadsKey(sessionId));
        dict?.Remove(downloadId, out _);
    }
}
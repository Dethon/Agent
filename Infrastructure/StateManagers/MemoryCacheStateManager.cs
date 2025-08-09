using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Caching.Memory;
using ModelContextProtocol.Server;

namespace Infrastructure.StateManagers;

public class MemoryCacheStateManager(IMemoryCache cache): IStateManager
{
    private readonly Lock _cacheLock = new();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, IMcpServer>>
        _subscribedResources = new();
    
    private static string GetTrackedDownloadsKey(string sessionId) =>
        $"TrackedDownloads_{sessionId}";
    private static string GetTrackedSearchResultsKey(string sessionId, int resultId) =>
        $"TrackedSearchResults_{sessionId}_{resultId}";
    
    public int[]? GetTrackedDownloads(string sessionId)
    {
        return cache
            .Get<ConcurrentDictionary<int, byte>>(GetTrackedDownloadsKey(sessionId))?.Keys
            .Order()
            .ToArray();
    }

    public void TrackDownload(string sessionId, int downloadId)
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

    public void UntrackDownload(string sessionId, int downloadId)
    {
        var dict = cache.Get<ConcurrentDictionary<int, byte>>(GetTrackedDownloadsKey(sessionId));
        dict?.Remove(downloadId, out _);
    }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IMcpServer>> GetSubscribedResources()
    {
        lock (_cacheLock)
        {
            return _subscribedResources.ToDictionary(
                kvp => kvp.Key,
                IReadOnlyDictionary<string, IMcpServer> (kvp) => kvp.Value.ToDictionary());
        }
    }

    public void SubscribeResource(string sessionId, string uri, IMcpServer server)
    {
        lock (_cacheLock)
        {
            _subscribedResources
                .GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, IMcpServer>())
                .TryAdd(uri, server);
        }
    }

    public void UnsubscribeResource(string sessionId, string uri)
    {
        var dict = _subscribedResources.GetValueOrDefault(sessionId);
        dict?.Remove(uri, out _);
    }

    public SearchResult? GetSearchResult(string sessionId, int downloadId)
    {
        return cache.Get<SearchResult>(GetTrackedSearchResultsKey(sessionId, downloadId));
    }

    public void AddSearchResult(string sessionId, SearchResult[] searchResults)
    {
        foreach (var result in searchResults)
        {
            cache.Set(GetTrackedSearchResultsKey(sessionId, result.Id), result, TimeSpan.FromDays(60));
        }
    }
}
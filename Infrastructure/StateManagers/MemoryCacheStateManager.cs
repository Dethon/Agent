using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Caching.Memory;

namespace Infrastructure.StateManagers;

public class MemoryCacheStateManager(IMemoryCache cache): IStateManager
{
    private static string GetTrackedDownloadsKey(string sessionId) =>
        $"TrackedDownloads_{sessionId}";
    private static string GetTrackedSearchResultsKey(string sessionId, int resultId) =>
        $"TrackedSearchResults_{sessionId}_{resultId}";
    private static string GetSubscribedResourceKey(string sessionId) =>
        $"SubscribedResources_{sessionId}";
    
    public int[]? GetTrackedDownloads(string sessionId)
    {
        return cache
            .Get<ConcurrentDictionary<int, byte>>(GetTrackedDownloadsKey(sessionId))?.Keys
            .Order()
            .ToArray();
    }

    public void TrackDownload(string sessionId, int downloadId)
    {
        var dict = cache.GetOrCreate(GetTrackedDownloadsKey(sessionId), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(60);
            return new ConcurrentDictionary<int, byte>();
        });
        dict?.TryAdd(downloadId, 1);
    }

    public void UntrackDownload(string sessionId, int downloadId)
    {
        var dict = cache.Get<ConcurrentDictionary<int, byte>>(GetTrackedDownloadsKey(sessionId));
        dict?.Remove(downloadId, out _);
    }

    public string[]? GetSubscribedResources(string sessionId)
    {
        return cache
            .Get<ConcurrentDictionary<string, byte>>(GetSubscribedResourceKey(sessionId))?.Keys
            .Order()
            .ToArray();
    }

    public void SubscribeResource(string sessionId, string uri)
    {
        var dict = cache.GetOrCreate(GetSubscribedResourceKey(sessionId), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(60);
            return new ConcurrentDictionary<string, byte>();
        });
        dict?.TryAdd(uri, 1);
    }

    public void UnsubscribeResource(string sessionId, string uri)
    {
        var dict = cache.Get<ConcurrentDictionary<string, byte>>(GetSubscribedResourceKey(sessionId));
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
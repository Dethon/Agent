using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Caching.Memory;

namespace Infrastructure.StateManagers;

public class SearchResultsManager(IMemoryCache cache) : ISearchResultsManager
{
    private readonly Lock _cacheLock = new();

    public SearchResult? Get(string sessionId, int downloadId)
    {
        return cache.Get<SearchResult>(GetTrackedSearchResultsKey(sessionId, downloadId));
    }

    public void Add(string sessionId, SearchResult[] searchResults)
    {
        lock (_cacheLock)
        {
            foreach (var result in searchResults)
            {
                cache.Set(GetTrackedSearchResultsKey(sessionId, result.Id), result, TimeSpan.FromDays(60));
            }
        }
    }

    private static string GetTrackedSearchResultsKey(string sessionId, int resultId)
    {
        return $"TrackedSearchResults_{sessionId}_{resultId}";
    }
}
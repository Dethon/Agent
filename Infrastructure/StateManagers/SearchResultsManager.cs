using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public class SearchResultsManager(IConnectionMultiplexer redis, TimeSpan expiry) : ISearchResultsManager
{
    private readonly IDatabase _db = redis.GetDatabase();

    private static string GetKey(string sessionId, int resultId)
    {
        return $"search:{sessionId}:{resultId}";
    }

    public SearchResult? Get(string sessionId, int downloadId)
    {
        var value = _db.StringGet(GetKey(sessionId, downloadId));
        return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<SearchResult>((string)value!);
    }

    public void Add(string sessionId, SearchResult[] searchResults)
    {
        foreach (var result in searchResults)
        {
            var json = JsonSerializer.Serialize(result);
            _db.StringSet(GetKey(sessionId, result.Id), json, expiry);
        }
    }
}
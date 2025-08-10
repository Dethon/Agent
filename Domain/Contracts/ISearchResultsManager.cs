using Domain.DTOs;

namespace Domain.Contracts;

public interface ISearchResultsManager
{
    SearchResult? Get(string sessionId, int downloadId);
    void Add(string sessionId, SearchResult[] searchResults);
}
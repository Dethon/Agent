using Domain.DTOs;

namespace Domain.Contracts;

public interface IStateManager
{
    int[]? GetTrackedDownloads(string sessionId);
    void TrackDownload(string sessionId, int downloadId);
    void UntrackDownload(string sessionId, int downloadId);
    
    string[]? GetSubscribedResources(string sessionId);
    void SubscribeResource(string sessionId, string uri);
    void UnsubscribeResource(string sessionId, string uri);
    
    SearchResult? GetSearchResult(string sessionId, int downloadId);
    void AddSearchResult(string sessionId, SearchResult[] searchResults);
}
using Domain.DTOs;
using ModelContextProtocol.Server;

namespace Domain.Contracts;

public interface IStateManager
{
    int[]? GetTrackedDownloads(string sessionId);
    void TrackDownload(string sessionId, int downloadId);
    void UntrackDownload(string sessionId, int downloadId);

    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IMcpServer>> GetSubscribedResources();
    void SubscribeResource(string sessionId, string uri, IMcpServer server);
    void UnsubscribeResource(string sessionId, string uri);
    
    SearchResult? GetSearchResult(string sessionId, int downloadId);
    void AddSearchResult(string sessionId, SearchResult[] searchResults);
}
namespace Domain.Contracts;

public interface ITrackedDownloadsManager
{
    int[]? Get(string sessionId);
    void Add(string sessionId, int downloadId);
    void Remove(string sessionId, int downloadId);
}
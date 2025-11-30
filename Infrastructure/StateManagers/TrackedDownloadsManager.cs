using Domain.Contracts;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public class TrackedDownloadsManager(IConnectionMultiplexer redis, TimeSpan expiry)
    : ITrackedDownloadsManager
{
    private readonly IDatabase _db = redis.GetDatabase();

    private static string GetKey(string sessionId)
    {
        return $"tracked:{sessionId}";
    }

    public int[]? Get(string sessionId)
    {
        var members = _db.SetMembers(GetKey(sessionId));
        if (members.Length == 0)
        {
            return null;
        }

        return members
            .Select(m => (int)m)
            .Order()
            .ToArray();
    }

    public void Add(string sessionId, int downloadId)
    {
        var key = GetKey(sessionId);
        _db.SetAdd(key, downloadId);
        _db.KeyExpire(key, expiry);
    }

    public void Remove(string sessionId, int downloadId)
    {
        _db.SetRemove(GetKey(sessionId), downloadId);
    }
}
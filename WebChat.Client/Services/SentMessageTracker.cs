using System.Collections.Concurrent;

namespace WebChat.Client.Services;

public sealed class SentMessageTracker
{
    private readonly ConcurrentDictionary<string, bool> _sentMessageIds = [];

    public string TrackNewMessage()
    {
        var id = Guid.NewGuid().ToString("N");
        _sentMessageIds[id] = true;
        return id;
    }

    public bool WasSentByThisClient(string? correlationId)
    {
        if (string.IsNullOrEmpty(correlationId))
        {
            return false;
        }

        return _sentMessageIds.ContainsKey(correlationId);
    }
}
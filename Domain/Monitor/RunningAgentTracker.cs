using System.Collections.Concurrent;

namespace Domain.Monitor;

public class RunningAgentTracker
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningAgents = [];

    public CancellationToken Track(string conversationId)
    {
        var cts = new CancellationTokenSource();
        _runningAgents[conversationId] = cts;
        return cts.Token;
    }

    public void Untrack(string conversationId)
    {
        if (_runningAgents.TryRemove(conversationId, out var cts))
        {
            cts.Dispose();
        }
    }

    public void Cancel(string conversationId)
    {
        if (_runningAgents.TryGetValue(conversationId, out var cts))
        {
            cts.Cancel();
        }
    }
}
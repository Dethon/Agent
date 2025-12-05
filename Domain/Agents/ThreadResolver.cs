using System.Collections.Concurrent;
using Microsoft.Agents.AI;

namespace Domain.Agents;

public class ThreadResolver
{
    private readonly ConcurrentDictionary<AgentKey, AgentThread> _cache = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public (long ChatId, long ThreadId)[] Threads =>
        _cache.Select(x => (x.Key.ChatId, x.Key.ThreadId)).ToArray();

    public async Task<AgentThread> Resolve(
        AgentKey key,
        Func<AgentThread> threadFactory,
        CancellationToken ct)
    {
        var thread = await GetThreadFromCache(key, threadFactory, ct);
        return thread ?? throw new InvalidOperationException(
            $"Thread for chatId:{key.ChatId}, threadId:{key.ThreadId} found in cache but was null.");
    }

    public void Clean(long chatId, long threadId)
    {
        _cache.Remove(new AgentKey(chatId, threadId), out _);
    }

    private async Task<AgentThread?> GetThreadFromCache(
        AgentKey key, Func<AgentThread> createThread, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var thread = _cache.GetValueOrDefault(key);
            if (thread is not null)
            {
                return thread;
            }

            thread = createThread();
            _cache[key] = thread;
            return thread;
        }
        finally
        {
            _lock.Release();
        }
    }
}
using System.Collections.Concurrent;

namespace Domain.Agents;

public readonly record struct AgentKey(long ChatId, long ThreadId);

public class AgentResolver
{
    private readonly ConcurrentDictionary<AgentKey, CancellableAiAgent> _cache = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public (long ChatId, long ThreadId)[] Agents =>
        _cache.Select(x => (x.Key.ChatId, x.Key.ThreadId)).ToArray();

    public async Task<CancellableAiAgent> Resolve(
        long? chatId,
        long? threadId,
        Func<CancellationToken, Task<CancellableAiAgent>> agentFactory,
        CancellationToken ct)
    {
        if (chatId is null || threadId is null)
        {
            return await agentFactory(ct);
        }

        var key = new AgentKey(chatId.Value, threadId.Value);
        var agent = await GetAgentFromCache(key, agentFactory, ct);
        return agent ?? throw new InvalidOperationException($"Jack for thread {chatId} found in cache but was null.");
    }

    public async Task Clean(long chatId, long threadId)
    {
        _cache.Remove(new AgentKey(chatId, threadId), out var agent);
        if (agent is not null)
        {
            await agent.DisposeAsync();
        }
    }

    private async Task<CancellableAiAgent?> GetAgentFromCache(
        AgentKey key, Func<CancellationToken, Task<CancellableAiAgent>> createAgent, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var agent = _cache.GetValueOrDefault(key);
            if (agent is not null)
            {
                return agent;
            }

            agent = await createAgent(ct);
            _cache[key] = agent;
            return agent;
        }
        finally
        {
            _lock.Release();
        }
    }
}
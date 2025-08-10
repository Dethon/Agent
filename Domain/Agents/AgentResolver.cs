using System.Collections.Concurrent;
using Domain.Contracts;

namespace Domain.Agents;

public class AgentResolver
{
    private readonly ConcurrentDictionary<string, IAgent> _cache = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private const string Separator = "<--SEPARATOR-->";

    public IEnumerable<(long ChatId, long ThreadId)> Agents =>
        _cache.Select(x =>
        {
            var parts = x.Key.Split(Separator);
            var chatId = long.Parse(parts[0]);
            var threadId = long.Parse(parts[1]);
            return (ChatId: chatId, ThreadId: threadId);
        });

    public async Task<IAgent> Resolve(
        long? chatId,
        long? threadId,
        Func<CancellationToken, Task<IAgent>> agentFactory,
        CancellationToken ct)
    {
        if (chatId is null || threadId is null)
        {
            return await agentFactory(ct);
        }

        var agent = await GetAgentFromCache(GetCacheKey(chatId.Value, threadId.Value), agentFactory, ct);
        if (agent is null)
        {
            throw new InvalidOperationException($"Agent for thread {chatId} found in cache but was null.");
        }

        return agent;
    }

    public async Task Clean(long chatId, long threadId)
    {
        _cache.Remove(GetCacheKey(chatId, threadId), out var agent);
        if (agent is not null)
        {
            await agent.DisposeAsync();
        }
    }

    private static string GetCacheKey(long chatId, long threadId)
    {
        return $"{chatId}{Separator}{threadId}";
    }

    private async Task<IAgent?> GetAgentFromCache(
        string key, Func<CancellationToken, Task<IAgent>> createAgent, CancellationToken ct)
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
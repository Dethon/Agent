using Domain.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace Domain.Agents;

public class AgentResolver(IMemoryCache cache)
{
    public async Task<IAgent> Resolve(
        int? chatId,
        Func<CancellationToken, Task<IAgent>> agentFactory,
        CancellationToken ct)
    {
        if (chatId is null)
        {
            return await agentFactory(ct);
        }

        var agent = await GetAgentFromCache(chatId.Value, () => agentFactory(ct));
        if (agent is null)
        {
            throw new InvalidOperationException($"Agent for thread {chatId} found in cache but was null.");
        }

        return agent;
    }

    private async Task<IAgent?> GetAgentFromCache(int threadId, Func<Task<IAgent>> createAgent)
    {
        return await cache.GetOrCreateAsync($"IAgent-{threadId}", cacheEntry =>
        {
            cacheEntry.SetAbsoluteExpiration(DateTimeOffset.UtcNow.AddMonths(2));
            return createAgent();
        });
    }
}
using Domain.Agents;
using Domain.Contracts;
using Infrastructure.Agents.ChatClients;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public sealed class RedisThreadStateStore(IConnectionMultiplexer redis) : IThreadStateStore
{
    public async Task DeleteAsync(AgentKey key)
    {
        var db = redis.GetDatabase();
        await db.KeyDeleteAsync(GetKey(key));
    }

    private static string GetKey(AgentKey agentKey)
    {
        return RedisChatMessageStore.GetRedisKey(agentKey);
    }
}
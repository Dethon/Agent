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

    public async Task<string?> GetMessagesAsync(string key)
    {
        var db = redis.GetDatabase();
        var value = await db.StringGetAsync(key);
        return value.HasValue ? value.ToString() : null;
    }

    public async Task SetMessagesAsync(string key, string json, TimeSpan expiry)
    {
        var db = redis.GetDatabase();
        await db.StringSetAsync(key, json, expiry);
    }

    private static string GetKey(AgentKey agentKey)
    {
        return RedisChatMessageStore.GetRedisKey(agentKey);
    }
}
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Microsoft.Extensions.AI;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public sealed class RedisThreadStateStore(IConnectionMultiplexer redis, TimeSpan expiration) : IThreadStateStore
{
    public async Task DeleteAsync(AgentKey key)
    {
        var db = redis.GetDatabase();
        await db.KeyDeleteAsync(key.ToString());
    }

    public ChatMessage[]? GetMessages(string key)
    {
        var db = redis.GetDatabase();
        var value = db.StringGet(key);
        return value.HasValue
            ? JsonSerializer.Deserialize<StoreState>(value.ToString())?.Messages
            : null;
    }

    public async Task SetMessagesAsync(string key, ChatMessage[] messages)
    {
        var db = redis.GetDatabase();
        var json = JsonSerializer.Serialize(new StoreState { Messages = messages });
        await db.StringSetAsync(key, json, expiration);
    }

    private sealed class StoreState
    {
        public ChatMessage[] Messages { get; init; } = [];
    }
}
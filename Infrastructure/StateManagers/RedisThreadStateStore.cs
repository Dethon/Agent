using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Microsoft.Extensions.AI;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public sealed class RedisThreadStateStore(IConnectionMultiplexer redis) : IThreadStateStore
{
    public async Task DeleteAsync(AgentKey key)
    {
        var db = redis.GetDatabase();
        await db.KeyDeleteAsync(key.ToString());
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

    public async Task<IReadOnlyList<ChatMessage>?> GetChatHistoryAsync(AgentKey key)
    {
        var json = await GetMessagesAsync(key.ToString());
        if (json is null)
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<StoreState>(json);
            return state?.Messages;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class StoreState
    {
        public List<ChatMessage> Messages { get; init; } = [];
    }
}
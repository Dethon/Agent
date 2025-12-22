using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public class RedisThreadStateStore(IConnectionMultiplexer redis) : IThreadStateStore
{
    private static readonly TimeSpan _expiry = TimeSpan.FromDays(30);
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<JsonElement?> LoadAsync(AgentKey key, CancellationToken ct)
    {
        var value = await _db.StringGetAsync(GetKey(key));
        if (!value.HasValue)
        {
            return null;
        }

        return JsonDocument.Parse(value.ToString()).RootElement;
    }

    public async Task SaveAsync(AgentKey key, JsonElement serializedThread, CancellationToken ct)
    {
        await _db.StringSetAsync(GetKey(key), serializedThread.GetRawText(), _expiry);
    }

    public async Task DeleteAsync(AgentKey key, CancellationToken ct)
    {
        await _db.KeyDeleteAsync(GetKey(key));
    }

    private static RedisKey GetKey(AgentKey key)
    {
        return $"thread:{key.ChatId}:{key.ThreadId}";
    }
}
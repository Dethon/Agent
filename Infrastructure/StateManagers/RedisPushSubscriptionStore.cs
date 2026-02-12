using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public sealed class RedisPushSubscriptionStore(IConnectionMultiplexer redis) : IPushSubscriptionStore
{
    private const string KeyPrefix = "push:subs:";

    public async Task SaveAsync(string userId, PushSubscriptionDto subscription, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var value = JsonSerializer.Serialize(new { subscription.P256dh, subscription.Auth });
        await db.HashSetAsync($"{KeyPrefix}{userId}", subscription.Endpoint, value);
    }

    public async Task RemoveAsync(string userId, string endpoint, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        await db.HashDeleteAsync($"{KeyPrefix}{userId}", endpoint);
    }

    public async Task RemoveByEndpointAsync(string endpoint, CancellationToken ct = default)
    {
        var server = redis.GetServers()[0];
        var db = redis.GetDatabase();

        await foreach (var key in server.KeysAsync(pattern: $"{KeyPrefix}*"))
        {
            await db.HashDeleteAsync(key, endpoint);
        }
    }

    public async Task<IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)>> GetAllAsync(
        CancellationToken ct = default)
    {
        var server = redis.GetServers()[0];
        var db = redis.GetDatabase();
        var results = new List<(string, PushSubscriptionDto)>();

        await foreach (var key in server.KeysAsync(pattern: $"{KeyPrefix}*"))
        {
            var userId = key.ToString()[KeyPrefix.Length..];
            var entries = await db.HashGetAllAsync(key);

            foreach (var entry in entries)
            {
                var data = JsonSerializer.Deserialize<JsonElement>(entry.Value.ToString());
                var sub = new PushSubscriptionDto(
                    entry.Name!,
                    data.GetProperty("P256dh").GetString()!,
                    data.GetProperty("Auth").GetString()!);
                results.Add((userId, sub));
            }
        }

        return results;
    }
}

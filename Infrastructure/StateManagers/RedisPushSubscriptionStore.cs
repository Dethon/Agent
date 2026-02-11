using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public sealed class RedisPushSubscriptionStore(IConnectionMultiplexer redis) : IPushSubscriptionStore
{
    private const string KeyPrefix = "push:subs:";

    private readonly IDatabase _db = redis.GetDatabase();

    public async Task SaveAsync(string userId, PushSubscriptionDto subscription, CancellationToken ct = default)
    {
        var value = JsonSerializer.Serialize(new { subscription.P256dh, subscription.Auth });
        await _db.HashSetAsync($"{KeyPrefix}{userId}", subscription.Endpoint, value);
    }

    public async Task RemoveAsync(string userId, string endpoint, CancellationToken ct = default)
    {
        await _db.HashDeleteAsync($"{KeyPrefix}{userId}", endpoint);
    }

    public async Task RemoveByEndpointAsync(string endpoint, CancellationToken ct = default)
    {
        var server = redis.GetServers()[0];

        await foreach (var key in server.KeysAsync(pattern: $"{KeyPrefix}*"))
        {
            await _db.HashDeleteAsync(key, endpoint);
        }
    }

    public async Task<IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)>> GetAllAsync(
        CancellationToken ct = default)
    {
        var server = redis.GetServers()[0];
        var results = new List<(string, PushSubscriptionDto)>();

        await foreach (var key in server.KeysAsync(pattern: $"{KeyPrefix}*"))
        {
            var userId = key.ToString()[KeyPrefix.Length..];
            var entries = await _db.HashGetAllAsync(key);

            results.AddRange(entries.Select(entry =>
            {
                var data = JsonSerializer.Deserialize<JsonElement>(entry.Value.ToString());
                var sub = new PushSubscriptionDto(
                    entry.Name!,
                    data.GetProperty("P256dh").GetString()!,
                    data.GetProperty("Auth").GetString()!);
                return (userId, sub);
            }));
        }

        return results;
    }
}

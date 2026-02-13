using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public sealed class RedisPushSubscriptionStore(IConnectionMultiplexer redis) : IPushSubscriptionStore
{
    private const string KeyPrefix = "push:subs:";

    public async Task SaveAsync(string userId, PushSubscriptionDto subscription, string spaceSlug = "default",
        CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var value = JsonSerializer.Serialize(new { subscription.P256dh, subscription.Auth, SpaceSlug = spaceSlug });
        await db.HashSetAsync($"{KeyPrefix}{userId}", subscription.Endpoint, value);
    }

    public async Task RemoveAsync(string userId, string endpoint, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        await db.HashDeleteAsync($"{KeyPrefix}{userId}", endpoint);
    }

    public async Task RemoveByEndpointAsync(string endpoint, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();

        await foreach (var key in ScanKeysAsync())
        {
            ct.ThrowIfCancellationRequested();
            await db.HashDeleteAsync(key, endpoint);
        }
    }

    public async Task<IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)>> GetAllAsync(
        CancellationToken ct = default)
    {
        return await CollectSubscriptionsAsync(ct);
    }

    public async Task<IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)>> GetBySpaceAsync(
        string spaceSlug, CancellationToken ct = default)
    {
        return await CollectSubscriptionsAsync(ct, spaceSlug);
    }

    private async Task<IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)>> CollectSubscriptionsAsync(
        CancellationToken ct, string? filterSpaceSlug = null)
    {
        var db = redis.GetDatabase();
        var results = new List<(string, PushSubscriptionDto)>();

        await foreach (var key in ScanKeysAsync())
        {
            ct.ThrowIfCancellationRequested();

            var userId = key.ToString()[KeyPrefix.Length..];
            var entries = await db.HashGetAllAsync(key);

            foreach (var entry in entries)
            {
                var data = JsonSerializer.Deserialize<JsonElement>(entry.Value.ToString());
                var spaceSlug = data.TryGetProperty("SpaceSlug", out var s) ? s.GetString() ?? "default" : "default";

                if (filterSpaceSlug is not null && spaceSlug != filterSpaceSlug)
                {
                    continue;
                }

                var sub = new PushSubscriptionDto(
                    entry.Name!,
                    data.GetProperty("P256dh").GetString()!,
                    data.GetProperty("Auth").GetString()!);
                results.Add((userId, sub));
            }
        }

        return results;
    }

    private async IAsyncEnumerable<RedisKey> ScanKeysAsync()
    {
        foreach (var endpoint in redis.GetEndPoints())
        {
            var server = redis.GetServer(endpoint);
            await foreach (var key in server.KeysAsync(pattern: $"{KeyPrefix}*"))
            {
                yield return key;
            }
        }
    }
}

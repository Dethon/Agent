using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public sealed class RedisPushSubscriptionStore(IConnectionMultiplexer redis) : IPushSubscriptionStore
{
    private const string KeyPrefix = "push:subs:";
    private const string EndpointPrefix = "push:ep:";
    private const string SpacePrefix = "push:space:";

    public async Task SaveAsync(string userId, PushSubscriptionDto subscription, string spaceSlug = "default",
        CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var endpointIndexKey = $"{EndpointPrefix}{subscription.Endpoint}";

        // Check if this endpoint was previously owned by a different user
        var previousOwner = await db.StringGetAsync(endpointIndexKey);
        if (previousOwner.HasValue && previousOwner.ToString() != userId)
        {
            await db.HashDeleteAsync($"{KeyPrefix}{previousOwner}", subscription.Endpoint);
        }

        // Check if spaceSlug changed for existing entry under same user
        if (previousOwner.HasValue)
        {
            var existing = await db.HashGetAsync($"{KeyPrefix}{previousOwner}", subscription.Endpoint);
            if (existing.HasValue)
            {
                var existingData = JsonSerializer.Deserialize<JsonElement>(existing.ToString());
                var oldSpace = existingData.TryGetProperty("SpaceSlug", out var s) ? s.GetString() ?? "default" : "default";
                if (oldSpace != spaceSlug)
                {
                    await db.SetRemoveAsync($"{SpacePrefix}{oldSpace}", subscription.Endpoint);
                }
            }
        }

        var value = JsonSerializer.Serialize(new { subscription.P256dh, subscription.Auth, SpaceSlug = spaceSlug });

        // Write primary hash + endpoint index + space set
        await db.HashSetAsync($"{KeyPrefix}{userId}", subscription.Endpoint, value);
        await db.StringSetAsync(endpointIndexKey, userId);
        await db.SetAddAsync($"{SpacePrefix}{spaceSlug}", subscription.Endpoint);
    }

    public async Task RemoveAsync(string userId, string endpoint, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();

        // Read spaceSlug from primary hash before deleting
        var existing = await db.HashGetAsync($"{KeyPrefix}{userId}", endpoint);
        if (existing.HasValue)
        {
            var data = JsonSerializer.Deserialize<JsonElement>(existing.ToString());
            var spaceSlug = data.TryGetProperty("SpaceSlug", out var s) ? s.GetString() ?? "default" : "default";
            await db.SetRemoveAsync($"{SpacePrefix}{spaceSlug}", endpoint);
        }

        await db.HashDeleteAsync($"{KeyPrefix}{userId}", endpoint);
        await db.KeyDeleteAsync($"{EndpointPrefix}{endpoint}");
    }

    public async Task RemoveByEndpointAsync(string endpoint, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var endpointIndexKey = $"{EndpointPrefix}{endpoint}";

        // O(1) lookup via endpoint index
        var userId = await db.StringGetAsync(endpointIndexKey);
        if (!userId.HasValue)
        {
            return;
        }

        // Read spaceSlug from primary hash
        var existing = await db.HashGetAsync($"{KeyPrefix}{userId}", endpoint);
        if (existing.HasValue)
        {
            var data = JsonSerializer.Deserialize<JsonElement>(existing.ToString());
            var spaceSlug = data.TryGetProperty("SpaceSlug", out var s) ? s.GetString() ?? "default" : "default";
            await db.SetRemoveAsync($"{SpacePrefix}{spaceSlug}", endpoint);
        }

        await db.HashDeleteAsync($"{KeyPrefix}{userId}", endpoint);
        await db.KeyDeleteAsync(endpointIndexKey);
    }

    public async Task<IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)>> GetAllAsync(
        CancellationToken ct = default)
    {
        // SCAN-based — only used in tests, not a hot path
        var db = redis.GetDatabase();
        var results = new List<(string, PushSubscriptionDto)>();

        await foreach (var key in ScanKeysAsync())
        {
            ct.ThrowIfCancellationRequested();

            var userId = key.ToString()[KeyPrefix.Length..];
            var entries = await db.HashGetAllAsync(key);

            results.AddRange(entries.Select(entry =>
            {
                var data = JsonSerializer.Deserialize<JsonElement>(entry.Value.ToString());
                return (userId, new PushSubscriptionDto(
                    entry.Name!,
                    data.GetProperty("P256dh").GetString()!,
                    data.GetProperty("Auth").GetString()!));
            }));
        }

        return results;
    }

    public async Task<IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)>> GetBySpaceAsync(
        string spaceSlug, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var endpoints = await db.SetMembersAsync($"{SpacePrefix}{spaceSlug}");
        var results = new List<(string, PushSubscriptionDto)>();

        foreach (var endpointValue in endpoints)
        {
            ct.ThrowIfCancellationRequested();

            var endpoint = endpointValue.ToString();
            var userId = await db.StringGetAsync($"{EndpointPrefix}{endpoint}");
            if (!userId.HasValue)
            {
                // Self-healing: stale index entry — remove from space set
                await db.SetRemoveAsync($"{SpacePrefix}{spaceSlug}", endpoint);
                continue;
            }

            var hashValue = await db.HashGetAsync($"{KeyPrefix}{userId}", endpoint);
            if (!hashValue.HasValue)
            {
                // Self-healing: stale index entries — clean up both
                await db.SetRemoveAsync($"{SpacePrefix}{spaceSlug}", endpoint);
                await db.KeyDeleteAsync($"{EndpointPrefix}{endpoint}");
                continue;
            }

            var data = JsonSerializer.Deserialize<JsonElement>(hashValue.ToString());
            results.Add((userId.ToString(), new PushSubscriptionDto(
                endpoint,
                data.GetProperty("P256dh").GetString()!,
                data.GetProperty("Auth").GetString()!)));
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

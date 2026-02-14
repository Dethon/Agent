using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using StackExchange.Redis;

namespace Infrastructure.Calendar;

public class RedisCalendarTokenStore(IConnectionMultiplexer redis) : ICalendarTokenStore
{
    private static readonly TimeSpan _defaultTtl = TimeSpan.FromDays(90);
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<OAuthTokens?> GetTokensAsync(string userId, CancellationToken ct = default)
    {
        var value = await _db.StringGetAsync(Key(userId));
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        return JsonSerializer.Deserialize<OAuthTokens>(value.ToString());
    }

    public async Task StoreTokensAsync(string userId, OAuthTokens tokens, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(tokens);
        await _db.StringSetAsync(Key(userId), json, _defaultTtl, false);
    }

    public async Task RemoveTokensAsync(string userId, CancellationToken ct = default)
    {
        await _db.KeyDeleteAsync(Key(userId));
    }

    public async Task<bool> HasTokensAsync(string userId, CancellationToken ct = default)
    {
        return await _db.KeyExistsAsync(Key(userId));
    }

    private static string Key(string userId) => $"calendar:tokens:{userId}";
}

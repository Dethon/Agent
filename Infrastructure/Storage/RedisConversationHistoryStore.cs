using Domain.Contracts;
using StackExchange.Redis;

namespace Infrastructure.Storage;

public class RedisConversationHistoryStore(IConnectionMultiplexer redis, TimeSpan? expiry = null)
    : IConversationHistoryStore
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly TimeSpan _expiry = expiry ?? TimeSpan.FromDays(7);

    public async Task<byte[]?> GetAsync(string conversationId, CancellationToken ct)
    {
        var value = await _db.StringGetAsync(GetKey(conversationId));
        return value.IsNullOrEmpty ? null : (byte[])value!;
    }

    public async Task SaveAsync(string conversationId, byte[] data, CancellationToken ct)
    {
        await _db.StringSetAsync(GetKey(conversationId), data, _expiry);
    }

    public async Task DeleteAsync(string conversationId, CancellationToken ct)
    {
        await _db.KeyDeleteAsync(GetKey(conversationId));
    }

    private static string GetKey(string conversationId)
    {
        return $"conversation:{conversationId}";
    }
}
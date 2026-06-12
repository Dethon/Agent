using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public sealed class RedisDownloadRoutingStore(IConnectionMultiplexer redis) : IDownloadRoutingStore
{
    private const string IndexKey = "download-routing";
    private static readonly TimeSpan _expiry = TimeSpan.FromDays(60);

    private readonly IDatabase _db = redis.GetDatabase();

    public async Task SetAsync(DownloadRouting routing, CancellationToken ct = default)
    {
        var transaction = _db.CreateTransaction();
        _ = transaction.StringSetAsync(EntryKey(routing.DownloadId), JsonSerializer.Serialize(routing), _expiry);
        _ = transaction.SetAddAsync(IndexKey, routing.DownloadId);
        await transaction.ExecuteAsync();
    }

    public async Task<IReadOnlyList<DownloadRouting>> ListAsync(CancellationToken ct = default)
    {
        var ids = await _db.SetMembersAsync(IndexKey);
        var entries = await Task.WhenAll(ids.Select(async id =>
        {
            var json = await _db.StringGetAsync(EntryKey((int)id));
            if (!json.IsNullOrEmpty)
            {
                return JsonSerializer.Deserialize<DownloadRouting>(json.ToString());
            }

            // Self-healing: stale index member — its entry has expired
            await _db.SetRemoveAsync(IndexKey, id);
            return null;
        }));
        return entries.Where(e => e is not null).Select(e => e!).ToList();
    }

    public async Task RemoveAsync(int downloadId, CancellationToken ct = default)
    {
        var transaction = _db.CreateTransaction();
        _ = transaction.KeyDeleteAsync(EntryKey(downloadId));
        _ = transaction.SetRemoveAsync(IndexKey, downloadId);
        await transaction.ExecuteAsync();
    }

    private static string EntryKey(int downloadId) => $"download-routing:{downloadId}";
}
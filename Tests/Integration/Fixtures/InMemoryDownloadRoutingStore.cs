using Domain.Contracts;
using Domain.DTOs;

namespace Tests.Integration.Fixtures;

public sealed class InMemoryDownloadRoutingStore : IDownloadRoutingStore
{
    private readonly List<DownloadRouting> _items = [];

    public Task SetAsync(DownloadRouting routing, CancellationToken ct = default)
    {
        _items.RemoveAll(r => r.DownloadId == routing.DownloadId);
        _items.Add(routing);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DownloadRouting>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DownloadRouting>>(_items.ToList());

    public Task RemoveAsync(int downloadId, CancellationToken ct = default)
    {
        _items.RemoveAll(r => r.DownloadId == downloadId);
        return Task.CompletedTask;
    }
}
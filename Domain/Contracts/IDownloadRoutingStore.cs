using Domain.DTOs;

namespace Domain.Contracts;

public interface IDownloadRoutingStore
{
    Task SetAsync(DownloadRouting routing, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadRouting>> ListAsync(CancellationToken ct = default);
    Task RemoveAsync(int downloadId, CancellationToken ct = default);
}
using Domain.DTOs;

namespace Domain.Contracts;

public interface IDownloadClient
{
    Task Download(string link, string savePath, string id, CancellationToken cancellationToken = default);
    Task Cleanup(string id, CancellationToken cancellationToken = default);

    Task<IEnumerable<DownloadItem>> RefreshDownloadItems(IEnumerable<DownloadItem> items,
        CancellationToken cancellationToken = default);

    Task<bool> IsDownloadComplete(string id, CancellationToken cancellationToken = default);
}
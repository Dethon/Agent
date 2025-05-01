using Domain.DTOs;

namespace Domain.Contracts;

public interface IDownloadClient
{
    Task Download(string link, string savePath, int id, CancellationToken cancellationToken = default);
    Task Cleanup(int id, CancellationToken cancellationToken = default);
    Task<IEnumerable<DownloadItem>> RefreshDownloadItems(IEnumerable<DownloadItem> items,
        CancellationToken cancellationToken = default);

    Task<DownloadItem?> GetDownloadItem(int id, CancellationToken cancellationToken = default);
    Task<bool> IsDownloadComplete(int id, CancellationToken cancellationToken = default);
}
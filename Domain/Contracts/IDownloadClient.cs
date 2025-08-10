using Domain.DTOs;

namespace Domain.Contracts;

public interface IDownloadClient
{
    Task Download(string link, string savePath, int id, CancellationToken cancellationToken = default);
    Task Cleanup(int id, CancellationToken cancellationToken = default);
    Task<DownloadItem?> GetDownloadItem(int id, CancellationToken cancellationToken = default);
}
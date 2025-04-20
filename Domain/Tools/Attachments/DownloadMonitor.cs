using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Attachments;

public abstract class DownloadMonitor(IDownloadClient client)
{
    private Dictionary<int, DownloadItem> Downloads { get; } = [];
    private readonly Lock _lLock = new();

    public void Add(SearchResult info, string savePath)
    {
        lock (_lLock)
        {
            if (!Downloads.TryAdd(info.Id, new DownloadItem
                {
                    Id = info.Id,
                    Title = info.Title,
                    Link = info.Link,
                    Category = info.Category,
                    Size = info.Size,
                    SavePath = savePath,
                    Seeders = info.Seeders,
                    Peers = info.Peers,
                    Status = DownloadStatus.Added
                }))
            {
                throw new Exception("Download already exists");
            }
        }
    }

    public async Task<bool> AreDownloadsInProgress(CancellationToken cancellationToken = default)
    {
        await Refresh(cancellationToken);
        lock (_lLock)
        {
            return Downloads.Any(x => x.Value.Status != DownloadStatus.Completed);
        }
    }

    public async Task<bool> Refresh(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
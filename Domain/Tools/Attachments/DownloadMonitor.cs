using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Attachments;

public class DownloadMonitor(IDownloadClient client)
{
    public Dictionary<int, DownloadItem> Downloads { get; private set; } = [];
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

    public async Task<bool> AreDownloadsPending(CancellationToken cancellationToken = default)
    {
        await Refresh(cancellationToken);
        lock (_lLock)
        {
            return Downloads.Count > 0;
        }
    }

    public async Task<int[]> PopCompletedDownloads(CancellationToken cancellationToken = default)
    {
        await Refresh(cancellationToken);
        lock (_lLock)
        {
            var completedIds = Downloads.Values
                .Where(x => x.Status == DownloadStatus.Completed)
                .Select(x => x.Id)
                .ToArray();
            Downloads = Downloads
                .Where(x => x.Value.Status != DownloadStatus.Completed)
                .ToDictionary(x => x.Key, x => x.Value);

            return completedIds;
        }
    }

    private async Task Refresh(CancellationToken cancellationToken = default)
    {
        var items = await client.RefreshDownloadItems(Downloads.Values, cancellationToken);
        lock (_lLock)
        {
            Downloads = items.ToDictionary(x => x.Id, x => x);
        }
    }
}
using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Exceptions;

namespace Domain.Tools.Attachments;

public class DownloadMonitor(IDownloadClient client)
{
    public ConcurrentDictionary<int, DownloadItem> Downloads { get; private set; } = [];

    public bool TryAdd(SearchResult info, string savePath)
    {
        return Downloads.TryAdd(info.Id, new DownloadItem
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
        });
    }

    public async Task<bool> TryAdd(int downloadId)
    {
        if (Downloads.ContainsKey(downloadId))
        {
            return false;
        }

        var item = await client.GetDownloadItem(downloadId);
        if (item is null)
        {
            throw new MissingDownloadException("Download missing");
        }

        return Downloads.TryAdd(item.Id, item);
    }

    public async Task<bool> AreDownloadsPending(CancellationToken cancellationToken = default)
    {
        await Refresh(cancellationToken);
        return !Downloads.IsEmpty;
    }

    public async Task<bool> PopCompletedDownload(int downloadId, CancellationToken cancellationToken = default)
    {
        await Refresh(cancellationToken);
        if (!Downloads.TryGetValue(downloadId, out var item))
        {
            throw new MissingDownloadException("Download missing. It probably got removed externally");
        }

        var isFinished = item.Status == DownloadStatus.Completed;
        if (isFinished)
        {
            Downloads.Remove(downloadId, out _);
        }

        return isFinished;
    }

    public async Task<int[]> PopCompletedDownloads(CancellationToken cancellationToken = default)
    {
        await Refresh(cancellationToken);
        var completedIds = Downloads.Values
            .Where(x => x.Status == DownloadStatus.Completed)
            .Select(x => x.Id)
            .ToArray();
        Downloads = new ConcurrentDictionary<int, DownloadItem>(Downloads
            .Where(x => x.Value.Status != DownloadStatus.Completed)
            .ToDictionary(x => x.Key, x => x.Value));

        return completedIds;
    }

    private async Task Refresh(CancellationToken cancellationToken = default)
    {
        var items = await client.RefreshDownloadItems(Downloads.Values, cancellationToken);
        Downloads = new ConcurrentDictionary<int, DownloadItem>(items.ToDictionary(x => x.Id, x => x));
    }
}
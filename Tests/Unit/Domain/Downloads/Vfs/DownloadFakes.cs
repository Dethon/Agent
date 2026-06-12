using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;

namespace Tests.Unit.Domain.Downloads.Vfs;

// Shared test doubles for the downloads VFS, its routing tools, and Task 10's watcher tests.
// Keep the public surface stable: FakeDownloadClient.Items/CleanedUp, FakeRoutingStore.Entries,
// and RecordingFileSystemClient.RemovedDirectories are read by all three test areas.
public static class DownloadFakes
{
    public static DownloadItem Item(int id, DownloadState state = DownloadState.InProgress) => new()
    {
        Id = id,
        Title = $"Download {id}",
        Link = $"magnet:{id}",
        State = state,
        Progress = state == DownloadState.Completed ? 1.0 : 0.5,
        DownSpeed = 1.5,
        UpSpeed = 0.25,
        Eta = 12,
        SavePath = $"/downloads/{id}",
        Size = 1024
    };

    public static DownloadsFileSystem BuildFileSystem(
        out FakeDownloadClient client,
        out FakeRoutingStore routing,
        out RecordingFileSystemClient disk)
    {
        client = new FakeDownloadClient();
        routing = new FakeRoutingStore();
        disk = new RecordingFileSystemClient();
        return new DownloadsFileSystem(client, routing, disk, new DownloadPathConfig("/downloads"));
    }

    public sealed class FakeDownloadClient : IDownloadClient
    {
        public List<DownloadItem> Items { get; } = new();
        public List<int> CleanedUp { get; } = new();

        public void Add(DownloadItem item)
        {
            Items.RemoveAll(i => i.Id == item.Id);
            Items.Add(item);
        }

        public Task Cleanup(int id, CancellationToken cancellationToken = default)
        {
            CleanedUp.Add(id);
            Items.RemoveAll(i => i.Id == id);
            return Task.CompletedTask;
        }

        public Task<DownloadItem?> GetDownloadItem(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.FirstOrDefault(i => i.Id == id));

        public Task<IReadOnlyList<DownloadItem>> GetDownloadItems(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DownloadItem>>(Items.ToList());

        public Task Download(string link, string savePath, int id, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class FakeRoutingStore : IDownloadRoutingStore
    {
        public List<DownloadRouting> Entries { get; } = new();

        public Task SetAsync(DownloadRouting routing, CancellationToken ct = default)
        {
            Entries.RemoveAll(r => r.DownloadId == routing.DownloadId);
            Entries.Add(routing);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DownloadRouting>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DownloadRouting>>(Entries.ToList());

        public Task RemoveAsync(int downloadId, CancellationToken ct = default)
        {
            Entries.RemoveAll(r => r.DownloadId == downloadId);
            return Task.CompletedTask;
        }
    }

    public sealed class RecordingFileSystemClient : IFileSystemClient
    {
        public List<string> RemovedDirectories { get; } = new();

        public Task RemoveDirectory(string path, CancellationToken cancellationToken = default)
        {
            RemovedDirectories.Add(path);
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, string[]>> DescribeDirectory(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Dictionary<string, string[]>());

        public Task<string[]> Glob(string basePath, string pattern, CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<string>());

        public Task Move(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveFile(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> MoveToTrash(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(path);
    }
}
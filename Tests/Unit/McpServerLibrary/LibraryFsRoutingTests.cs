using System.Linq;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using McpServerLibrary.McpTools;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace Tests.Unit.McpServerLibrary;

public class LibraryFsRoutingTests
{
    private readonly FakeDownloadClient _client = new();
    private readonly FakeDownloadRoutingStore _routing = new();
    private readonly RecordingFileSystemClient _fs = new();

    private DownloadsFileSystem BuildDownloads() =>
        new(_client, _routing, _fs, new DownloadPathConfig("/downloads"));

    private static DownloadItem Item(int id) => new()
    {
        Id = id,
        Title = $"Download {id}",
        Link = $"magnet:{id}",
        State = DownloadState.InProgress,
        Progress = 0.5,
        DownSpeed = 1.5,
        UpSpeed = 0.25,
        Eta = 12,
        SavePath = $"/downloads/{id}",
        Size = 1024
    };

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task FsRead_DownloadsFilesystem_ReadsStatus()
    {
        _client.Add(Item(42));
        var tool = new FsReadTool(BuildDownloads());

        var result = await tool.McpRun("42/status.json", null, null, "downloads");

        var text = Text(result);
        text.ShouldContain("id");
        text.ShouldContain("42");
        text.ShouldContain("Download 42");
    }

    [Fact]
    public async Task FsRead_WithoutDownloadsFilesystem_IsUnsupported()
    {
        var tool = new FsReadTool(BuildDownloads());

        var result = await tool.McpRun("anything.txt", null, null, null);

        Text(result).ShouldContain("unsupported_operation");
    }

    [Fact]
    public async Task FsDelete_DownloadsFilesystem_CleansUp()
    {
        _client.Add(Item(42));
        var tool = new FsDeleteTool(BuildDownloads());

        var result = await tool.McpRun("42", "downloads");

        Text(result).ShouldContain("removed");
        _client.CleanedUp.ShouldContain(42);
    }

    [Fact]
    public async Task FsDelete_WithoutDownloadsFilesystem_IsUnsupported()
    {
        var tool = new FsDeleteTool(BuildDownloads());

        var result = await tool.McpRun("42", null);

        Text(result).ShouldContain("unsupported_operation");
    }

    [Fact]
    public async Task FsGlob_DownloadsFilesystem_ListsViaEngine()
    {
        _client.Add(Item(42));
        _client.Add(Item(7));
        var tool = new FsGlobTool(_fs, new LibraryPathConfig("/library"), BuildDownloads());

        var result = await tool.McpRun("**", "/", "downloads");

        Text(result).ShouldContain("/42/status.json");
    }

    private sealed class FakeDownloadClient : IDownloadClient
    {
        private readonly Dictionary<int, DownloadItem> _items = new();
        public List<int> CleanedUp { get; } = new();

        public void Add(DownloadItem item) => _items[item.Id] = item;

        public Task Cleanup(int id, CancellationToken cancellationToken = default)
        {
            CleanedUp.Add(id);
            _items.Remove(id);
            return Task.CompletedTask;
        }

        public Task<DownloadItem?> GetDownloadItem(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.GetValueOrDefault(id));

        public Task<IReadOnlyList<DownloadItem>> GetDownloadItems(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DownloadItem>>(_items.Values.ToList());

        public Task Download(string link, string savePath, int id, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeDownloadRoutingStore : IDownloadRoutingStore
    {
        private readonly Dictionary<int, DownloadRouting> _routings = new();

        public Task SetAsync(DownloadRouting routing, CancellationToken ct = default)
        {
            _routings[routing.DownloadId] = routing;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DownloadRouting>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DownloadRouting>>(_routings.Values.ToList());

        public Task RemoveAsync(int downloadId, CancellationToken ct = default)
        {
            _routings.Remove(downloadId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingFileSystemClient : IFileSystemClient
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
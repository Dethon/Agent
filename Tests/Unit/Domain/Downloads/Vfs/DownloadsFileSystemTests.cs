using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Shouldly;

namespace Tests.Unit.Domain.Downloads.Vfs;

public class DownloadsFileSystemTests
{
    private readonly FakeDownloadClient _client = new();
    private readonly FakeDownloadRoutingStore _routing = new();
    private readonly RecordingFileSystemClient _fs = new();

    private DownloadsFileSystem Build() =>
        new(_client, _routing, _fs, new DownloadPathConfig("/downloads"));

    private static DownloadItem Item(int id, string title, DownloadState state) => new()
    {
        Id = id,
        Title = title,
        Link = $"magnet:{id}",
        State = state,
        Progress = state == DownloadState.Completed ? 1.0 : 0.5,
        DownSpeed = 1.5,
        UpSpeed = 0.25,
        Eta = 12,
        SavePath = $"/downloads/{id}",
        Size = 1024
    };

    [Fact]
    public async Task Contract_NameAndUnsupportedOps()
    {
        var fs = Build();

        fs.ShouldBeAssignableTo<IFileSystemBackend>();
        fs.FilesystemName.ShouldBe("downloads");

        var move = await fs.MoveAsync("42", "7", CancellationToken.None);
        move.ShouldBeOfType<FsResult<FsMoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var exec = await fs.ExecAsync("42", "anything", null, CancellationToken.None);
        exec.ShouldBeOfType<FsResult<FsExecResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var create = await fs.CreateAsync("42/status.json", "{}", true, true, CancellationToken.None);
        create.ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var copy = await fs.CopyAsync("42", "7", false, true, CancellationToken.None);
        copy.ShouldBeOfType<FsResult<FsCopyResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var edit = await fs.EditAsync("42/status.json", new[] { new TextEdit("a", "b") }, CancellationToken.None);
        edit.ShouldBeOfType<FsResult<FsEditResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
    }

    [Fact]
    public async Task Glob_ListsDownloadDirsAndStatusFiles()
    {
        _client.Add(Item(42, "Big Buck Bunny", DownloadState.InProgress));
        _client.Add(Item(7, "Sintel", DownloadState.Completed));
        var fs = Build();

        var all = (await fs.GlobAsync("/", "**", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        all.Entries.ShouldContain("/42/");
        all.Entries.ShouldContain("/42/status.json");
        all.Entries.ShouldContain("/7/");
        all.Entries.ShouldContain("/7/status.json");

        var statusOnly = (await fs.GlobAsync("/", "*/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        statusOnly.Entries.ShouldBe(new[] { "/42/status.json", "/7/status.json" }, ignoreOrder: true);
        statusOnly.Entries.ShouldNotContain("/42/");
    }

    [Fact]
    public async Task Read_StatusJson_RendersDownloadState()
    {
        _client.Add(Item(42, "Big Buck Bunny", DownloadState.InProgress));
        var fs = Build();

        var read = (await fs.ReadAsync("42/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        read.Content.ShouldContain("42");
        read.Content.ShouldContain("InProgress");
        read.Content.ShouldContain("Big Buck Bunny");

        var missing = await fs.ReadAsync("99/status.json", null, null, CancellationToken.None);
        missing.ShouldBeOfType<FsResult<FsReadResult>.Err>().Error.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public async Task Info_ReportsExistence()
    {
        _client.Add(Item(42, "Big Buck Bunny", DownloadState.InProgress));
        var fs = Build();

        var root = (await fs.InfoAsync("/", CancellationToken.None)).ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        root.Exists.ShouldBeTrue();
        root.IsDirectory.ShouldBe(true);

        var dir = (await fs.InfoAsync("42", CancellationToken.None)).ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        dir.Exists.ShouldBeTrue();
        dir.IsDirectory.ShouldBe(true);

        var statusFile = (await fs.InfoAsync("42/status.json", CancellationToken.None)).ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        statusFile.Exists.ShouldBeTrue();
        statusFile.IsDirectory.ShouldBe(false);

        var missing = (await fs.InfoAsync("99", CancellationToken.None)).ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        missing.Exists.ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_DownloadDir_CleansUpEverything()
    {
        _client.Add(Item(42, "Big Buck Bunny", DownloadState.InProgress));
        await _routing.SetAsync(new DownloadRouting
        {
            DownloadId = 42,
            Title = "Big Buck Bunny",
            Context = new ConversationContext("agent", "conv", "user", new ReplyTarget("library", "conv"))
        }, CancellationToken.None);
        var fs = Build();

        var delete = (await fs.DeleteAsync("42", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Ok>().Value;
        delete.Status.ShouldBe("removed");

        _client.CleanedUp.ShouldContain(42);
        (await _routing.ListAsync(CancellationToken.None)).ShouldBeEmpty();
        _fs.RemovedDirectories.ShouldContain("/downloads/42");
    }

    [Fact]
    public async Task Delete_StatusFileOrUnknown_IsRejected()
    {
        _client.Add(Item(42, "Big Buck Bunny", DownloadState.InProgress));
        var fs = Build();

        var statusDelete = await fs.DeleteAsync("42/status.json", CancellationToken.None);
        statusDelete.ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var unknownDelete = await fs.DeleteAsync("99", CancellationToken.None);
        unknownDelete.ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("not_found");
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
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools.Downloads.Vfs;
using Shouldly;
using static Tests.Unit.Domain.Downloads.Vfs.DownloadFakes;

namespace Tests.Unit.Domain.Downloads.Vfs;

public class DownloadsOverlayTests : IDisposable
{
    private readonly string _libraryRoot;
    private readonly FakeDownloadClient _client;
    private readonly FakeRoutingStore _routing;
    private readonly RecordingFileSystemClient _fs;
    private readonly DownloadsOverlay _sut;

    public DownloadsOverlayTests()
    {
        _libraryRoot = Path.Combine(Path.GetTempPath(), $"overlay-{Guid.NewGuid()}");
        Directory.CreateDirectory(_libraryRoot);
        _sut = BuildOverlay(_libraryRoot, out _client, out _routing, out _fs);
    }

    public void Dispose()
    {
        if (Directory.Exists(_libraryRoot))
        {
            Directory.Delete(_libraryRoot, true);
        }
    }

    [Fact]
    public async Task TryRead_StatusJson_RendersStateWithoutSavePath()
    {
        _client.Add(Item(42));

        var read = (await _sut.TryReadAsync("downloads/42/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        read.Content.ShouldContain("42");
        read.Content.ShouldContain("InProgress");
        read.Content.ShouldContain("Download 42");
        read.Content.ShouldNotContain("savePath");

        var missing = await _sut.TryReadAsync("downloads/99/status.json", CancellationToken.None);
        missing.ShouldBeOfType<FsResult<FsReadResult>.Err>().Error.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public async Task TryRead_NonOverlayPath_ReturnsNull()
    {
        (await _sut.TryReadAsync("Movies/film.mkv", CancellationToken.None)).ShouldBeNull();
        (await _sut.TryReadAsync("downloads/42/payload.mkv", CancellationToken.None)).ShouldBeNull();
        (await _sut.TryReadAsync("downloads", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task TryRead_AbsolutePathUnderLibraryRoot_IsNormalized()
    {
        _client.Add(Item(42));

        var read = await _sut.TryReadAsync(
            Path.Combine(_libraryRoot, "downloads", "42", "status.json"), CancellationToken.None);
        read.ShouldBeOfType<FsResult<FsReadResult>.Ok>();
    }

    [Fact]
    public async Task TryInfo_OwnsStatusFilesAndLiveDownloadDirs()
    {
        _client.Add(Item(42));

        var dir = (await _sut.TryInfoAsync("downloads/42", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        dir.Exists.ShouldBeTrue();
        dir.IsDirectory.ShouldBe(true);

        var statusFile = (await _sut.TryInfoAsync("downloads/42/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        statusFile.Exists.ShouldBeTrue();
        statusFile.IsDirectory.ShouldBe(false);

        var deadStatus = (await _sut.TryInfoAsync("downloads/99/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        deadStatus.Exists.ShouldBeFalse();

        (await _sut.TryInfoAsync("downloads/99", CancellationToken.None)).ShouldBeNull();
        (await _sut.TryInfoAsync("Movies", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task GlobEntries_MatchRootAndBasePathPatterns()
    {
        _client.Add(Item(42));
        _client.Add(Item(7, DownloadState.Completed));

        var all = await _sut.GlobEntriesAsync("", "**", CancellationToken.None);
        all.ShouldContain("downloads/42/");
        all.ShouldContain("downloads/42/status.json");
        all.ShouldContain("downloads/7/");
        all.ShouldContain("downloads/7/status.json");

        var statusOnly = await _sut.GlobEntriesAsync("", "downloads/*/status.json", CancellationToken.None);
        statusOnly.ShouldBe(new[] { "downloads/42/status.json", "downloads/7/status.json" }, ignoreOrder: true);

        var dirsOnly = await _sut.GlobEntriesAsync("", "downloads/*/", CancellationToken.None);
        dirsOnly.ShouldBe(new[] { "downloads/42/", "downloads/7/" }, ignoreOrder: true);

        var based = await _sut.GlobEntriesAsync("downloads", "*/status.json", CancellationToken.None);
        based.ShouldBe(new[] { "downloads/42/status.json", "downloads/7/status.json" }, ignoreOrder: true);

        var elsewhere = await _sut.GlobEntriesAsync("Movies", "**", CancellationToken.None);
        elsewhere.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_ActiveDownload_CleansUpEverything()
    {
        _client.Add(Item(42));
        await _routing.SetAsync(new DownloadRouting
        {
            DownloadId = 42,
            Title = "Download 42",
            Context = new ConversationContext("agent", "conv", "user", new ReplyTarget("library", "conv"))
        }, CancellationToken.None);

        var delete = (await _sut.DeleteAsync("downloads/42", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Ok>().Value;
        delete.Status.ShouldBe("removed");

        _client.CleanedUp.ShouldContain(42);
        (await _routing.ListAsync(CancellationToken.None)).ShouldBeEmpty();
        _fs.RemovedDirectories.ShouldContain(Path.Combine(_libraryRoot, "downloads", "42"));
    }

    [Fact]
    public async Task Delete_LeftoverDirWithoutTorrent_RemovesDirAndStaleRouting()
    {
        Directory.CreateDirectory(Path.Combine(_libraryRoot, "downloads", "99"));
        await _routing.SetAsync(new DownloadRouting
        {
            DownloadId = 99,
            Title = "Stale",
            Context = new ConversationContext("agent", "conv", "user", new ReplyTarget("library", "conv"))
        }, CancellationToken.None);

        var delete = (await _sut.DeleteAsync("downloads/99", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Ok>().Value;
        delete.Status.ShouldBe("removed");

        _client.CleanedUp.ShouldBeEmpty();
        (await _routing.ListAsync(CancellationToken.None)).ShouldBeEmpty();
        _fs.RemovedDirectories.ShouldContain(Path.Combine(_libraryRoot, "downloads", "99"));
    }

    [Fact]
    public async Task Delete_RejectsNonDownloadTargets()
    {
        _client.Add(Item(42));

        (await _sut.DeleteAsync("downloads/42/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        (await _sut.DeleteAsync("Movies/film.mkv", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        (await _sut.DeleteAsync("downloads/123", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public void IsVirtualPath_TrueOnlyForStatusFiles()
    {
        _sut.IsVirtualPath("downloads/42/status.json").ShouldBeTrue();
        _sut.IsVirtualPath("/downloads/42/status.json").ShouldBeTrue();
        _sut.IsVirtualPath(Path.Combine(_libraryRoot, "downloads", "42", "status.json")).ShouldBeTrue();
        _sut.IsVirtualPath("downloads/42").ShouldBeFalse();
        _sut.IsVirtualPath("downloads/42/file.mkv").ShouldBeFalse();
        _sut.IsVirtualPath("Movies/status.json").ShouldBeFalse();
    }
}
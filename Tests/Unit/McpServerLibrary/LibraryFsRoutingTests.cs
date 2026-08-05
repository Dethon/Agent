using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Shouldly;
using static Tests.Unit.Domain.Downloads.Vfs.DownloadFakes;

namespace Tests.Unit.McpServerLibrary;

// The library's media mount is one disk root with a downloads overlay on top. These cover the
// routing between the two: which paths the overlay owns, which fall through to disk, and which it
// refuses because they are a rendered view rather than a file.
public class LibraryFsRoutingTests : IDisposable
{
    private readonly string _libraryRoot;
    private readonly FakeDownloadClient _client;
    private readonly RecordingFileSystemClient _fs;
    private readonly MediaLibraryDiskFileSystem _media;

    public LibraryFsRoutingTests()
    {
        _libraryRoot = Path.Combine(Path.GetTempPath(), $"library-{Guid.NewGuid()}");
        Directory.CreateDirectory(_libraryRoot);
        var overlay = BuildOverlay(_libraryRoot, out _client, out _, out _fs);
        _media = new MediaLibraryDiskFileSystem(_fs, new LibraryPathConfig(_libraryRoot), overlay);
    }

    public void Dispose()
    {
        if (Directory.Exists(_libraryRoot))
        {
            Directory.Delete(_libraryRoot, true);
        }
    }

    [Fact]
    public async Task Read_StatusPath_ReadsStatus()
    {
        _client.Add(Item(42));

        var read = (await _media.ReadAsync("downloads/42/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;

        read.Content.ShouldContain("42");
        read.Content.ShouldContain("Download 42");
    }

    [Fact]
    public async Task Read_NonStatusPath_IsUnsupported()
    {
        (await _media.ReadAsync("Movies/film.mkv", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
        (await _media.ReadAsync("downloads/42/payload.mkv", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
    }

    [Fact]
    public async Task Delete_DownloadDir_CleansUp()
    {
        _client.Add(Item(42));

        (await _media.DeleteAsync("downloads/42", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Ok>().Value.Status.ShouldBe("removed");

        _client.CleanedUp.ShouldContain(42);
    }

    [Fact]
    public async Task Delete_NonDownloadPath_IsUnsupported()
    {
        (await _media.DeleteAsync("Movies/film.mkv", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
    }

    [Fact]
    public async Task Glob_MergesVirtualEntriesWithDiskResults()
    {
        _client.Add(Item(42));
        _fs.GlobResults.Add($"{_libraryRoot}/downloads/42/");
        _fs.GlobResults.Add($"{_libraryRoot}/downloads/42/payload.mkv");

        var glob = (await _media.GlobAsync("", "**", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        glob.Entries.ShouldContain("downloads/42/status.json");
        glob.Entries.ShouldContain("downloads/42/payload.mkv");
        glob.Entries.Count(e => e == "downloads/42/").ShouldBe(1);
        glob.Total.ShouldBe(3);
    }

    // status.json is rendered from live download state, so moving or copying it would produce a
    // stale snapshot under a name that still looks live.
    [Fact]
    public async Task VirtualStatusPath_CannotBeMovedCopiedOrWritten()
    {
        (await _media.MoveAsync("downloads/42/status.json", "Movies/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
        (await _media.MoveAsync("Movies/film.mkv", "downloads/42/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
        (await _media.CopyAsync("downloads/42/status.json", "Movies/x.json", false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCopyResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
    }

    [Fact]
    public async Task Info_LiveDownloadDirIsVirtual_OtherPathsFallThroughToDisk()
    {
        _client.Add(Item(42));
        File.WriteAllText(Path.Combine(_libraryRoot, "real.txt"), "x");

        var dir = (await _media.InfoAsync("downloads/42", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        dir.Exists.ShouldBeTrue();
        dir.IsDirectory.ShouldBe(true);

        var file = (await _media.InfoAsync("real.txt", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        file.Exists.ShouldBeTrue();
        file.IsDirectory.ShouldBe(false);

        var missing = (await _media.InfoAsync("downloads/99", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        missing.Exists.ShouldBeFalse();
    }
}
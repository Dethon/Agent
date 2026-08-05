using System.Text;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;
using static Tests.Unit.Domain.Downloads.Vfs.DownloadFakes;

namespace Tests.Unit.Domain.Downloads.Vfs;

// The media mount is the disk root plus the downloads overlay, and every hole in that seam has the
// same shape: an operation the overlay never saw reaches the disk underneath and writes, moves or
// lists something the overlay claims to own.
public class MediaLibraryFileSystemTests : IDisposable
{
    private readonly string _libraryRoot;
    private readonly FakeDownloadClient _client;
    private readonly RecordingFileSystemClient _disk;
    private readonly MediaLibraryDiskFileSystem _sut;

    public MediaLibraryFileSystemTests()
    {
        _libraryRoot = Path.Combine(Path.GetTempPath(), $"media-fs-{Guid.NewGuid()}");
        Directory.CreateDirectory(_libraryRoot);
        var overlay = BuildOverlay(_libraryRoot, out _client, out _, out _disk);
        _sut = new MediaLibraryDiskFileSystem(_disk, new LibraryPathConfig(_libraryRoot), overlay);
    }

    public void Dispose()
    {
        if (Directory.Exists(_libraryRoot))
        {
            Directory.Delete(_libraryRoot, true);
        }

        GC.SuppressFinalize(this);
    }

    // The read-only refusal used to sit on WriteBlobAsync alone, so a cross-mount copy — which
    // streams through WriteChunksAsync — put a real file where the virtual status.json is. That file
    // was then invisible (the overlay shadows reads, Merge dedupes globs) and unremovable (delete on
    // the status path answers "read-only"), which is as stuck as a file gets.
    [Fact]
    public async Task WriteChunks_OntoTheVirtualStatusFile_IsRefusedAndWritesNothing()
    {
        _client.Add(Item(42));

        var write = await Should.ThrowAsync<NotSupportedException>(() => _sut.WriteChunksAsync(
            "downloads/42/status.json", Chunks("stale snapshot"),
            overwrite: true, createDirectories: true, CancellationToken.None));

        write.Message.ShouldContain("read-only");
        File.Exists(Path.Combine(_libraryRoot, "downloads", "42", "status.json")).ShouldBeFalse();
    }

    // The same refusal on the way out: ReadBlobAsync already answered it, and the streamed half of
    // the same operation must not quietly serve the disk's idea of the file instead.
    [Fact]
    public async Task ReadChunks_OfTheVirtualStatusFile_IsRefused()
    {
        _client.Add(Item(42));

        var read = Should.Throw<NotSupportedException>(() =>
            _sut.ReadChunksAsync("downloads/42/status.json", CancellationToken.None));

        read.Message.ShouldContain("read-only");
        await Task.CompletedTask;
    }

    // The disk resolves '.' and '..' before it writes, so a dotted spelling of the status path lands
    // on exactly the file the overlay shadows. The classifier used to read the caller's literal
    // spelling, so every refusal on this mount was one '.' away from being switched off.
    [Theory]
    [InlineData("downloads/42/./status.json")]
    [InlineData("downloads/43/../42/status.json")]
    public async Task WriteChunks_OntoADottedSpellingOfTheVirtualStatusFile_IsRefused(string path)
    {
        _client.Add(Item(42));

        var write = await Should.ThrowAsync<NotSupportedException>(() => _sut.WriteChunksAsync(
            path, Chunks("stale snapshot"), overwrite: true, createDirectories: true, CancellationToken.None));

        write.Message.ShouldContain("read-only");
        File.Exists(Path.Combine(_libraryRoot, "downloads", "42", "status.json")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("downloads/42/./status.json")]
    [InlineData("downloads/43/../42/status.json")]
    public async Task BlobWrite_OntoADottedSpellingOfTheVirtualStatusFile_IsRefused(string path)
    {
        _client.Add(Item(42));

        var write = await _sut.WriteBlobAsync(path, Convert.ToBase64String("stale"u8.ToArray()),
            offset: 0, overwrite: true, createDirectories: true, CancellationToken.None);

        write.ShouldBeOfType<FsResult<FsBlobWriteResult>.Err>()
            .Error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        File.Exists(Path.Combine(_libraryRoot, "downloads", "42", "status.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task WriteChunks_OntoAPlainMediaPath_StillWrites()
    {
        var written = await _sut.WriteChunksAsync(
            "Movies/notes.txt", Chunks("hello"),
            overwrite: true, createDirectories: true, CancellationToken.None);

        written.ShouldBe(5);
        (await File.ReadAllTextAsync(Path.Combine(_libraryRoot, "Movies", "notes.txt"))).ShouldBe("hello");
    }

    // Deleting downloads/<id> is the documented cancel, but moving it is not: qBittorrent keeps
    // writing, recreates the directory it lost, and a later delete then cancels and cleans the
    // recreated one while the moved copy is orphaned. The refusal covers the download directory and
    // anything above it, because moving the parent takes the same directory with it.
    [Theory]
    [InlineData("downloads/42")]
    [InlineData("downloads")]
    [InlineData("/downloads/42")]
    [InlineData("downloads/./42")]
    [InlineData("downloads/43/../42")]
    [InlineData("./downloads")]
    public async Task Move_APathHoldingALiveDownload_IsRefused(string source)
    {
        _client.Add(Item(42));

        var move = await _sut.MoveAsync(source, "Movies/42", CancellationToken.None);

        var error = move.ShouldBeOfType<FsResult<FsMoveResult>.Err>().Error;
        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Message.ShouldContain("active download");
    }

    // The other half of the same boundary. A payload file inside a live download is not "above" the
    // download directory, so the ancestor rule never saw it: moving it out left qBittorrent
    // rewriting the file it still owns and the moved copy orphaned. And a move whose destination
    // lands inside the directory puts the file where delete-as-cancel destroys it.
    [Theory]
    [InlineData("downloads/42/payload.mkv", "Movies/payload.mkv")]
    [InlineData("Movies/payload.mkv", "downloads/42/payload.mkv")]
    [InlineData("Movies/payload.mkv", "downloads/42")]
    public async Task Move_AcrossALiveDownloadsBoundary_IsRefused(string source, string destination)
    {
        _client.Add(Item(42));

        var move = await _sut.MoveAsync(source, destination, CancellationToken.None);

        var error = move.ShouldBeOfType<FsResult<FsMoveResult>.Err>().Error;
        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Message.ShouldContain("active download");
    }

    [Fact]
    public async Task Move_APathWithNoLiveDownloadUnderIt_StillMoves()
    {
        _client.Add(Item(42));

        (await _sut.MoveAsync("Movies/old", "Movies/new", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveResult>.Ok>();
        (await _sut.MoveAsync("downloads/7", "Movies/7", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveResult>.Ok>();
    }

    // The disk glob relativizes an absolute pattern against the root it matches from; the overlay
    // used to match the caller's original spelling, whose leading root no mount-relative candidate
    // can ever have. So an absolute pattern listed the real files and silently omitted every
    // virtual status.json — the one thing on this mount that only the overlay knows about.
    [Theory]
    [InlineData("downloads/*/status.json", "downloads/42/status.json")]
    [InlineData("downloads/*", "downloads/42/")]
    public async Task Glob_WithAnAbsolutePattern_StillListsWhatTheOverlayOwns(string relative, string expected)
    {
        _client.Add(Item(42));
        _disk.GlobResults.Add(Path.Combine(_libraryRoot, "downloads", "42", "payload.mkv"));

        var absolute = Path.Combine(_libraryRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        var glob = (await _sut.GlobAsync("", absolute, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        glob.Entries.ShouldContain(expected);
        glob.Entries.ShouldContain("downloads/42/payload.mkv");
    }

    [Fact]
    public async Task Glob_WithARelativePattern_IsUnchanged()
    {
        _client.Add(Item(42));

        var glob = (await _sut.GlobAsync("downloads", "*/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        glob.Entries.ShouldContain("downloads/42/status.json");
    }

    // The same boundary, crossed by the one path that never calls this backend's MoveAsync: a
    // cross-mount move streams the payload files out and then deletes the source, and that delete is
    // the overlay's cancel. So the move the same-mount path refuses outright used to complete —
    // download cancelled, files gone — with a copy left behind on the other mount.
    [Fact]
    public async Task CrossMountMove_OfALiveDownload_IsRefusedAndCancelsNothing()
    {
        _client.Add(Item(42));
        var destination = new Mock<IFileSystemBackend>();
        destination.SetupGet(b => b.FilesystemName).Returns("vault");

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/media/downloads/42"))
            .Returns(Resolved(_sut, "downloads/42"));
        registry.Setup(r => r.Resolve("/vault/42"))
            .Returns(Resolved(destination.Object, "42"));

        var result = await new VfsMoveTool(registry.Object).RunAsync("/media/downloads/42", "/vault/42");

        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.UnsupportedOperation);
        result["message"]!.GetValue<string>().ShouldContain("active download");
        _client.CleanedUp.ShouldBeEmpty();
        _client.Items.ShouldContain(i => i.Id == 42);
        destination.Verify(b => b.WriteChunksAsync(It.IsAny<string>(),
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The reverse direction: a move landing inside a live download's directory is destroyed the
    // moment the download is cancelled, whichever mount it came from.
    [Fact]
    public async Task CrossMountMove_IntoALiveDownload_IsRefused()
    {
        _client.Add(Item(42));
        var source = new Mock<IFileSystemBackend>();
        source.SetupGet(b => b.FilesystemName).Returns("vault");
        source.Setup(b => b.InfoAsync("note.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsInfoResult>.Ok(
                new FsInfoResult { Exists = true, Path = "note.md", IsDirectory = false }));

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/vault/note.md")).Returns(Resolved(source.Object, "note.md"));
        registry.Setup(r => r.Resolve("/media/downloads/42/note.md"))
            .Returns(Resolved(_sut, "downloads/42/note.md"));

        var result = await new VfsMoveTool(registry.Object)
            .RunAsync("/vault/note.md", "/media/downloads/42/note.md");

        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.UnsupportedOperation);
        result["message"]!.GetValue<string>().ShouldContain("active download");
        source.Verify(b => b.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CrossMountMove_OfAPlainMediaFile_StillTransfers()
    {
        _client.Add(Item(42));
        await File.WriteAllTextAsync(Path.Combine(_libraryRoot, "notes.txt"), "hello");
        var destination = new Mock<IFileSystemBackend>();
        destination.SetupGet(b => b.FilesystemName).Returns("vault");
        destination.Setup(b => b.WriteChunksAsync("notes.txt",
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5L);

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/media/notes.txt")).Returns(Resolved(_sut, "notes.txt"));
        registry.Setup(r => r.Resolve("/vault/notes.txt")).Returns(Resolved(destination.Object, "notes.txt"));

        await new VfsMoveTool(registry.Object).RunAsync("/media/notes.txt", "/vault/notes.txt");

        // The transfer runs: only paths a live download owns are refused. (The move still ends on
        // this mount's delete rule — fs_delete here removes download directories only — which is
        // the pre-existing behaviour and not what this test is about.)
        destination.Verify(b => b.WriteChunksAsync("notes.txt",
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static FsResult<FileSystemResolution> Resolved(IFileSystemBackend backend, string relativePath) =>
        new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(backend, relativePath, ""));

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(string content)
    {
        await Task.CompletedTask;
        yield return Encoding.UTF8.GetBytes(content);
    }
}
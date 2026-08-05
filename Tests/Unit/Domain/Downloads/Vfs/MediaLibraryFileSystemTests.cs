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

    // The landing refusal used to sit on WriteBlobAsync alone, so a cross-mount copy — which
    // streams through WriteChunksAsync — put a real file where the virtual status.json is. That file
    // was then invisible (the overlay shadows reads, Merge dedupes globs) and unremovable (delete on
    // the status path answers "read-only"), which is as stuck as a file gets. A write to a live
    // download's status file lands inside that download's directory, so the landing reason covers
    // it: the file dies with the download, which is the more useful thing to tell the agent.
    [Fact]
    public async Task WriteChunks_OntoTheVirtualStatusFile_IsRefusedAndWritesNothing()
    {
        _client.Add(Item(42));

        var write = await Should.ThrowAsync<FileSystemOperationException>(() => _sut.WriteChunksAsync(
            "downloads/42/status.json", Chunks("stale snapshot"),
            overwrite: true, createDirectories: true, CancellationToken.None));

        write.Error.Message.ShouldContain("lands inside");
        File.Exists(Path.Combine(_libraryRoot, "downloads", "42", "status.json")).ShouldBeFalse();
    }

    // A leftover status file is an ordinary file, so both halves of a write reach the disk.
    [Fact]
    public async Task WritesOntoALeftoverStatusFile_Succeed()
    {
        await WriteLeftoverStatus();

        (await _sut.WriteBlobAsync("downloads/99/status.json", Convert.ToBase64String("ranged"u8.ToArray()),
                offset: 0, overwrite: true, createDirectories: false, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobWriteResult>.Ok>();

        (await _sut.WriteChunksAsync("downloads/99/status.json", Chunks("streamed"),
            overwrite: true, createDirectories: false, CancellationToken.None)).ShouldBe(8);

        (await File.ReadAllTextAsync(Path.Combine(_libraryRoot, "downloads", "99", "status.json")))
            .ShouldBe("streamed");
    }

    // Both halves of a read of a live download's status file answer the same thing: it is a
    // rendered view, so read it as text. The streamed half says so through the typed filesystem
    // exception, which is the only shape its signature has.
    [Fact]
    public async Task ReadsOfALiveDownloadsStatusFile_AreRefusedTheSameWay()
    {
        _client.Add(Item(42));

        var ranged = (await _sut.ReadBlobAsync("downloads/42/status.json", 0, 10, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobReadResult>.Err>().Error;
        ranged.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        ranged.Message.ShouldContain("fs_read");

        var streamed = await Should.ThrowAsync<FileSystemOperationException>(
            () => Collect("downloads/42/status.json"));
        streamed.Error.ErrorCode.ShouldBe(ranged.ErrorCode);
        streamed.Error.Message.ShouldBe(ranged.Message);
    }

    // A status.json no live download owns is a leftover: an ordinary file the disk owns. A ranged
    // read already served it while the streamed read refused it as a virtual file, so the same file
    // read differently depending on which side of the MCP seam the caller sat on.
    [Fact]
    public async Task ReadsOfALeftoverStatusFile_BothServeTheRealFile()
    {
        await WriteLeftoverStatus();

        var ranged = (await _sut.ReadBlobAsync("downloads/99/status.json", 0, 1024, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobReadResult>.Ok>().Value;

        Encoding.UTF8.GetString(Convert.FromBase64String(ranged.ContentBase64)).ShouldContain("stale");
        (await Collect("downloads/99/status.json")).ShouldContain("stale");
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

        var write = await Should.ThrowAsync<FileSystemOperationException>(() => _sut.WriteChunksAsync(
            path, Chunks("stale snapshot"), overwrite: true, createDirectories: true, CancellationToken.None));

        write.Error.Message.ShouldContain("lands inside");
        File.Exists(Path.Combine(_libraryRoot, "downloads", "42", "status.json")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("downloads/42/./status.json")]
    [InlineData("downloads/43/../42/status.json")]
    [InlineData("downloads/42/./book.epub")]
    public async Task BlobWrite_OntoADottedSpellingOfTheVirtualStatusFile_IsRefused(string path)
    {
        _client.Add(Item(42));

        var write = await _sut.WriteBlobAsync(path, Convert.ToBase64String("stale"u8.ToArray()),
            offset: 0, overwrite: true, createDirectories: true, CancellationToken.None);

        var error = write.ShouldBeOfType<FsResult<FsBlobWriteResult>.Err>().Error;
        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Message.ShouldContain("lands inside");
        Directory.Exists(Path.Combine(_libraryRoot, "downloads", "42")).ShouldBeFalse();
    }

    // A directory whose name merely looks like an id is an ordinary directory, so a write into it
    // is an ordinary write.
    [Theory]
    [InlineData("042")]
    [InlineData("+42")]
    public async Task BlobWrite_IntoALookalikeDownloadDirectory_Succeeds(string dir)
    {
        _client.Add(Item(42));

        (await _sut.WriteBlobAsync($"downloads/{dir}/book.epub", Convert.ToBase64String("book"u8.ToArray()),
                offset: 0, overwrite: true, createDirectories: true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobWriteResult>.Ok>();
    }

    [Fact]
    public async Task BlobWrite_IntoAnAbsoluteSpellingOfALiveDownloadDirectory_IsRefused()
    {
        _client.Add(Item(42));

        var write = await _sut.WriteBlobAsync(
            Path.Combine(_libraryRoot, "downloads", "42", "book.epub"),
            Convert.ToBase64String("book"u8.ToArray()),
            offset: 0, overwrite: true, createDirectories: true, CancellationToken.None);

        write.ShouldBeOfType<FsResult<FsBlobWriteResult>.Err>().Error.Message.ShouldContain("lands inside");
    }

    // A read intent classifies one path, whichever way that path is spelled: the dotted and
    // absolute forms resolve to the same file, and a directory whose name merely looks like an id
    // is an ordinary directory no download owns.
    [Theory]
    [InlineData("downloads/42/./status.json")]
    [InlineData("downloads/43/../42/status.json")]
    public async Task StreamedReadOfALiveStatusFile_IsRefusedWhicheverWayItIsSpelled(string path)
    {
        _client.Add(Item(42));

        var streamed = await Should.ThrowAsync<FileSystemOperationException>(() => Collect(path));

        streamed.Error.Message.ShouldContain("fs_read");
    }

    [Fact]
    public async Task StreamedReadOfAnAbsoluteSpellingOfALiveStatusFile_IsRefused()
    {
        _client.Add(Item(42));

        var streamed = await Should.ThrowAsync<FileSystemOperationException>(
            () => Collect(Path.Combine(_libraryRoot, "downloads", "42", "status.json")));

        streamed.Error.Message.ShouldContain("fs_read");
    }

    // 'downloads/042' is a real directory on disk, not download 42, so a status.json under it is an
    // ordinary file both reads serve.
    [Theory]
    [InlineData("042")]
    [InlineData("+42")]
    public async Task ReadsOfAStatusFileUnderALookalikeId_ServeTheRealFile(string dir)
    {
        _client.Add(Item(42));
        var file = Path.Combine(_libraryRoot, "downloads", dir, "status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "{\"real\":true}");

        (await _sut.ReadBlobAsync($"downloads/{dir}/status.json", 0, 1024, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobReadResult>.Ok>();
        (await Collect($"downloads/{dir}/status.json")).ShouldContain("real");
    }

    // The only text this mount reads is a status file; everything else on it is bytes. That is true
    // of a leftover status file too — it reads as the ordinary file it is.
    [Theory]
    [InlineData("downloads/42")]
    [InlineData("downloads/42/payload.mkv")]
    [InlineData("downloads/99")]
    [InlineData("Movies/film.mkv")]
    public async Task TextRead_OfAPathThatIsNotAStatusFile_IsRefused(string path)
    {
        _client.Add(Item(42));

        var error = (await _sut.ReadAsync(path, null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Err>().Error;

        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Message.ShouldContain("status.json");
    }

    [Fact]
    public async Task TextRead_OfALiveDownloadsStatusFile_ReportsItsState()
    {
        _client.Add(Item(42));

        var read = (await _sut.ReadAsync("downloads/42/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;

        read.Content.ShouldContain("InProgress");
        read.Content.ShouldContain("etaMinutes");
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

    // A real file left at downloads/<id>/status.json after its download is gone used to be a ghost:
    // the overlay answered every info for that path with its virtual file (exists=false once no
    // download owns the id) and refused every delete as read-only, so nothing could see or remove
    // it. With no live item the disk underneath is the truth.
    [Fact]
    public async Task Info_ALeftoverStatusFileWithNoLiveDownload_ComesFromDisk()
    {
        var leftover = Path.Combine(_libraryRoot, "downloads", "99", "status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        await File.WriteAllTextAsync(leftover, "{\"stale\":true}");

        var info = (await _sut.InfoAsync("downloads/99/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;

        info.Exists.ShouldBeTrue();
        info.IsDirectory.ShouldBe(false);
    }

    [Fact]
    public async Task Delete_ALeftoverStatusFileWithNoLiveDownload_RemovesTheRealFile()
    {
        var leftover = Path.Combine(_libraryRoot, "downloads", "99", "status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        await File.WriteAllTextAsync(leftover, "{\"stale\":true}");

        (await _sut.DeleteAsync("downloads/99/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Ok>();

        _disk.TrashedPaths.ShouldContain(p => p.EndsWith("status.json", StringComparison.Ordinal));
    }

    // The virtual file wins for as long as a download owns the id: it is a rendered view, not a
    // file, so info reports the rendered size and delete still refuses it.
    [Fact]
    public async Task StatusFileOfALiveDownload_StaysVirtual()
    {
        _client.Add(Item(42));

        var info = (await _sut.InfoAsync("downloads/42/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        info.Exists.ShouldBeTrue();
        info.Size.ShouldNotBeNull();

        (await _sut.DeleteAsync("downloads/42/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>()
            .Error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        _disk.TrashedPaths.ShouldBeEmpty();
    }

    // fs_info answers Exists=true for an active download's directory before qBittorrent has
    // created it on disk, so the glob's not_found short-circuit contradicted info on the very path
    // the mount's prose advertises. When the disk has nothing, the overlay still owns its answer.
    [Fact]
    public async Task Glob_AnActiveDownloadDirectoryNotYetOnDisk_ListsTheVirtualStatusFile()
    {
        _client.Add(Item(42));
        _disk.ThrowIfBaseMissing = true;

        var glob = (await _sut.GlobAsync("downloads/42", "*", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        glob.Entries.ShouldContain("downloads/42/status.json");
    }

    [Fact]
    public async Task Glob_AMissingDirectoryNoDownloadOwns_IsStillNotFound()
    {
        _disk.ThrowIfBaseMissing = true;

        (await _sut.GlobAsync("Movies/nope", "*", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Err>()
            .Error.ErrorCode.ShouldBe(ToolError.Codes.NotFound);
    }

    // A move into a live download's directory is refused because delete-as-cancel destroys
    // whatever landed there — and a copy or blob write lands a file in exactly the same place.
    // The original survives, but the copy is silently destroyed on cancel, so the boundary
    // refuses the way in for every write-shaped operation. The way out stays open: copying from
    // a live download leaves the download's own files untouched.
    [Fact]
    public async Task Copy_IntoALiveDownloadDirectory_IsRefused()
    {
        _client.Add(Item(42));
        await File.WriteAllTextAsync(Path.Combine(_libraryRoot, "book.epub"), "book");

        (await _sut.CopyAsync("book.epub", "downloads/42/book.epub", overwrite: false,
                createDirectories: true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCopyResult>.Err>()
            .Error.Message.ShouldContain("active download");

        File.Exists(Path.Combine(_libraryRoot, "downloads", "42", "book.epub")).ShouldBeFalse();
    }

    [Fact]
    public async Task BlobWrite_IntoALiveDownloadDirectory_IsRefused()
    {
        _client.Add(Item(42));

        (await _sut.WriteBlobAsync("downloads/42/book.epub", Convert.ToBase64String("book"u8.ToArray()),
                offset: 0, overwrite: true, createDirectories: true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobWriteResult>.Err>()
            .Error.Message.ShouldContain("active download");

        File.Exists(Path.Combine(_libraryRoot, "downloads", "42", "book.epub")).ShouldBeFalse();
    }

    [Fact]
    public async Task WriteChunks_IntoALiveDownloadDirectory_IsRefused()
    {
        _client.Add(Item(42));

        var thrown = await Should.ThrowAsync<FileSystemOperationException>(() => _sut.WriteChunksAsync(
            "downloads/42/book.epub", Chunks("book"), overwrite: true, createDirectories: true,
            CancellationToken.None));

        thrown.Error.Message.ShouldContain("active download");
        File.Exists(Path.Combine(_libraryRoot, "downloads", "42", "book.epub")).ShouldBeFalse();
    }

    [Fact]
    public async Task Copy_OutOfALiveDownloadDirectory_StillCopies()
    {
        _client.Add(Item(42));
        var payload = Path.Combine(_libraryRoot, "downloads", "42", "book.epub");
        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
        await File.WriteAllTextAsync(payload, "book");

        (await _sut.CopyAsync("downloads/42/book.epub", "Books/book.epub", overwrite: false,
                createDirectories: true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCopyResult>.Ok>();

        File.Exists(Path.Combine(_libraryRoot, "Books", "book.epub")).ShouldBeTrue();
    }

    // The leftover fix made a dead download's real status.json visible and removable; reading it
    // still answered not_found (fs_read) or the read-only refusal (fs_blob_read) — info said the
    // file exists while both read paths denied it. With no live item the disk is the truth for
    // reads too.
    [Fact]
    public async Task Read_ALeftoverStatusFileWithNoLiveDownload_ServesTheRealFile()
    {
        var leftover = Path.Combine(_libraryRoot, "downloads", "99", "status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        await File.WriteAllTextAsync(leftover, "{\"stale\":true}");

        var read = (await _sut.ReadAsync("downloads/99/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;

        read.Content.ShouldContain("stale");
    }

    [Fact]
    public async Task BlobRead_ALeftoverStatusFileWithNoLiveDownload_ServesTheRealFile()
    {
        var leftover = Path.Combine(_libraryRoot, "downloads", "99", "status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        await File.WriteAllTextAsync(leftover, "{\"stale\":true}");

        var blob = (await _sut.ReadBlobAsync("downloads/99/status.json", 0, 1024, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobReadResult>.Ok>().Value;

        Encoding.UTF8.GetString(Convert.FromBase64String(blob.ContentBase64)).ShouldContain("stale");
    }

    [Fact]
    public async Task Read_AStatusPathWithNoLiveDownloadAndNoFile_IsNotFound()
    {
        (await _sut.ReadAsync("downloads/99/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Err>()
            .Error.ErrorCode.ShouldBe(ToolError.Codes.NotFound);
    }

    [Fact]
    public async Task BlobRead_OfALiveDownloadsStatusFile_IsStillRefused()
    {
        _client.Add(Item(42));

        (await _sut.ReadBlobAsync("downloads/42/status.json", 0, 10, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobReadResult>.Err>()
            .Error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
    }

    private static FsResult<FileSystemResolution> Resolved(IFileSystemBackend backend, string relativePath) =>
        new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(backend, relativePath, ""));

    private async Task<string> Collect(string path)
    {
        var bytes = new List<byte>();
        await foreach (var chunk in _sut.ReadChunksAsync(path, CancellationToken.None))
        {
            bytes.AddRange(chunk.ToArray());
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private async Task WriteLeftoverStatus()
    {
        var leftover = Path.Combine(_libraryRoot, "downloads", "99", "status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        await File.WriteAllTextAsync(leftover, "{\"stale\":true}");
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(string content)
    {
        await Task.CompletedTask;
        yield return Encoding.UTF8.GetBytes(content);
    }
}
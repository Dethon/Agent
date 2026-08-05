using System.Text;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
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

    [Fact]
    public async Task WriteChunks_OntoAPlainMediaPath_StillWrites()
    {
        var written = await _sut.WriteChunksAsync(
            "Movies/notes.txt", Chunks("hello"),
            overwrite: true, createDirectories: true, CancellationToken.None);

        written.ShouldBe(5);
        (await File.ReadAllTextAsync(Path.Combine(_libraryRoot, "Movies", "notes.txt"))).ShouldBe("hello");
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(string content)
    {
        await Task.CompletedTask;
        yield return Encoding.UTF8.GetBytes(content);
    }
}
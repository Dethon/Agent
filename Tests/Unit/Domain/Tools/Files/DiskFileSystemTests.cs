using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Clients;
using Shouldly;
using static Tests.Unit.Domain.Downloads.Vfs.DownloadFakes;

namespace Tests.Unit.Domain.Tools.Files;

public class DiskFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"diskfs-{Guid.NewGuid():N}");

    public DiskFileSystemTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private DiskFileSystem PlainRoot() =>
        new("media", new LocalFileSystemClient(), new LibraryPathConfig(_root));

    private TextDiskFileSystem TextRoot() =>
        new("vault", new LocalFileSystemClient(), new LibraryPathConfig(_root), [".md", ".txt"]);

    [Fact]
    public void BothShapes_AreBackends()
    {
        PlainRoot().ShouldBeAssignableTo<IFileSystemBackend>();
        TextRoot().ShouldBeAssignableTo<IFileSystemBackend>();
    }

    // A plain disk root has no text tooling, so create, edit and search are left unoverridden and
    // the base answers them. That is what keeps /media from advertising operations it never had.
    [Fact]
    public async Task PlainRoot_TextOperations_ReturnUnsupported()
    {
        var fs = PlainRoot();

        (await fs.CreateAsync("note.md", "hi", false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
        (await fs.EditAsync("note.md", [], CancellationToken.None))
            .ShouldBeOfType<FsResult<FsEditResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
        (await fs.SearchAsync("q", false, null, null, null, 50, 1,
                global::Domain.DTOs.VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
    }

    // Neither shape can run a command; only the sandbox adds that.
    [Fact]
    public async Task NeitherShape_SupportsExec()
    {
        (await PlainRoot().ExecAsync("", "ls", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
        (await TextRoot().ExecAsync("", "ls", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
    }

    [Fact]
    public async Task TextRoot_CreateReadEditSearch_RoundTripsOnDisk()
    {
        var fs = TextRoot();

        (await fs.CreateAsync("notes/todo.md", "# Todo\nbuy milk\n", false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Ok>();

        var read = (await fs.ReadAsync("notes/todo.md", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        read.Content.ShouldContain("buy milk");

        (await fs.EditAsync("notes/todo.md", [new global::Domain.DTOs.TextEdit("milk", "bread")], CancellationToken.None))
            .ShouldBeOfType<FsResult<FsEditResult>.Ok>();

        var search = (await fs.SearchAsync("bread", false, null, "/", null, 50, 1,
                global::Domain.DTOs.VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        search.TotalMatches.ShouldBe(1);
    }

    [Fact]
    public async Task Info_ReportsDiskMetadata()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "abc");

        var info = (await PlainRoot().InfoAsync("a.txt", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;

        info.Exists.ShouldBeTrue();
        info.IsDirectory.ShouldBe(false);
        info.Size.ShouldBe(3);
    }

    // Containment comes from the one path jail, so a sibling whose name extends the root is outside.
    [Fact]
    public async Task Info_SiblingDirectoryWithRootPrefix_ReturnsInvalidArgument()
    {
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            File.WriteAllText(Path.Combine(sibling, "secret.txt"), "leak");

            (await PlainRoot().InfoAsync(Path.Combine(sibling, "secret.txt"), CancellationToken.None))
                .ShouldBeOfType<FsResult<FsInfoResult>.Err>().Error.ErrorCode
                .ShouldBe(ToolError.Codes.InvalidArgument);
        }
        finally
        {
            Directory.Delete(sibling, true);
        }
    }

    [Fact]
    public async Task BlobChunks_RoundTripBytes()
    {
        var fs = PlainRoot();
        var bytes = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();

        await fs.WriteChunksAsync("blob.bin", Chunks(bytes), overwrite: true, createDirectories: true, CancellationToken.None);

        var readBack = new List<byte>();
        await foreach (var chunk in fs.ReadChunksAsync("blob.bin", CancellationToken.None))
        {
            readBack.AddRange(chunk.ToArray());
        }

        readBack.ShouldBe(bytes);
    }

    // The library's downloads view: an active download surfaces a virtual status.json that is not
    // on disk, and the same call on a root without an overlay finds nothing.
    [Fact]
    public async Task WithOverlay_ServesTheDownloadsView()
    {
        var overlay = BuildOverlay(_root, out var client, out _, out _);
        client.Add(Item(42));
        var fs = new DiskFileSystem("media", new LocalFileSystemClient(), new LibraryPathConfig(_root), overlay);

        var read = (await fs.ReadAsync("downloads/42/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        read.Content.ShouldContain("42");

        var glob = (await fs.GlobAsync("", "**/*", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        glob.Entries.ShouldContain("downloads/42/status.json");
    }

    [Fact]
    public async Task WithOverlay_VirtualStatusFileCannotBeMovedOrCopied()
    {
        var overlay = BuildOverlay(_root, out var client, out _, out _);
        client.Add(Item(42));
        var fs = new DiskFileSystem("media", new LocalFileSystemClient(), new LibraryPathConfig(_root), overlay);

        (await fs.MoveAsync("downloads/42/status.json", "elsewhere.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
        (await fs.CopyAsync("downloads/42/status.json", "elsewhere.json", false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCopyResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
    }

    [Fact]
    public async Task WithoutOverlay_ReadingAFileWithNoTextToolingIsUnsupported()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "abc");

        (await PlainRoot().ReadAsync("a.txt", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(byte[] bytes)
    {
        await Task.CompletedTask;
        yield return bytes;
    }
}
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Clients;
using Shouldly;

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
        new("media", "A plain disk root.", new LocalFileSystemClient(), new LibraryPathConfig(_root));

    private TextDiskFileSystem TextRoot() =>
        new("vault", "A text disk root.", new LocalFileSystemClient(), new LibraryPathConfig(_root),
            [".md", ".txt"]);

    // A plain disk root has no text tooling, so read, create, edit and search are left unoverridden
    // and the base answers them. That is what keeps /media from advertising operations it never had.
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

    // The allowed extensions are the rule about what this root's files are, and a blob write is a
    // write like any other. It used to bypass them entirely: a cross-mount copy streams through the
    // chunk path, so any binary could land under any name in a .md-only vault — and overwrite a
    // note on the way. The refusal is the envelope both shapes of the operation answer with.
    [Fact]
    public async Task TextRoot_BlobWriteOfADisallowedExtension_IsRefused()
    {
        var fs = TextRoot();

        var write = await fs.WriteBlobAsync("photo.png", Convert.ToBase64String([1, 2, 3]), 0,
            overwrite: true, createDirectories: true, CancellationToken.None);

        write.ShouldBeOfType<FsResult<FsBlobWriteResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.InvalidArgument);
        File.Exists(Path.Combine(_root, "photo.png")).ShouldBeFalse();
    }

    [Fact]
    public async Task TextRoot_BlobChunksOfADisallowedExtension_AreRefused()
    {
        var fs = TextRoot();

        var thrown = await Should.ThrowAsync<FileSystemOperationException>(() => fs.WriteChunksAsync(
            "notes/photo.png", Chunks([1, 2, 3]),
            overwrite: true, createDirectories: true, CancellationToken.None));

        thrown.Error.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        thrown.Error.Retryable.ShouldBeFalse();
        File.Exists(Path.Combine(_root, "notes", "photo.png")).ShouldBeFalse();
    }

    [Fact]
    public async Task TextRoot_BlobChunksOfAnAllowedExtension_StillWrite()
    {
        await TextRoot().WriteChunksAsync("notes/todo.md", Chunks("hi"u8.ToArray()),
            overwrite: true, createDirectories: true, CancellationToken.None);

        (await File.ReadAllTextAsync(Path.Combine(_root, "notes", "todo.md"))).ShouldBe("hi");
    }

    // A plain disk root has no such rule, so its blob writes take whatever bytes they are given.
    [Fact]
    public async Task PlainRoot_BlobChunksOfAnyExtension_StillWrite()
    {
        await PlainRoot().WriteChunksAsync("photo.png", Chunks([1, 2, 3]),
            overwrite: true, createDirectories: true, CancellationToken.None);

        File.Exists(Path.Combine(_root, "photo.png")).ShouldBeTrue();
    }

    // Reading bytes as text needs a rule about which files are text, and that rule is what the text
    // shape adds. A plain disk root has none, so it does not read — and does not advertise fs_read.
    [Fact]
    public async Task PlainRoot_DoesNotRead()
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
using System.Text;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsTransferDirectoryTests
{
    [Fact]
    public async Task TransferDirectoryAsync_CrossFsCopy_RecordsPerEntryResults()
    {
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.GlobAsync("src", "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = ["src/a.md", "src/sub/b.md"], Truncated = false, Total = 2
            }));
        src.Setup(b => b.ReadChunksAsync("src/a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("A")));
        src.Setup(b => b.ReadChunksAsync("src/sub/b.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("BB")));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var srcRes = new FileSystemResolution(src.Object, "src");
        var dstRes = new FileSystemResolution(dst.Object, "dst");

        var result = await VfsCopyTool.TransferDirectoryAsync(
            srcRes, dstRes, "/vault/src", "/sandbox/dst",
            overwrite: false, createDirectories: true, deleteSource: false, CancellationToken.None);

        result["status"]!.GetValue<string>().ShouldBe("ok");
        result["summary"]!["transferred"]!.GetValue<int>().ShouldBe(2);
        result["summary"]!["failed"]!.GetValue<int>().ShouldBe(0);
        result["entries"]!.AsArray().Count.ShouldBe(2);
        dst.Verify(b => b.WriteChunksAsync("dst/a.md",
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            false, true, It.IsAny<CancellationToken>()), Times.Once);
        dst.Verify(b => b.WriteChunksAsync("dst/sub/b.md",
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            false, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TransferDirectoryAsync_PartialFailure_StatusIsPartial()
    {
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.GlobAsync("src", "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = ["src/a.md", "src/b.md"], Truncated = false, Total = 2
            }));
        src.Setup(b => b.ReadChunksAsync("src/a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("A")));
        src.Setup(b => b.ReadChunksAsync("src/b.md", It.IsAny<CancellationToken>()))
            .Throws(new IOException("boom"));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var srcRes = new FileSystemResolution(src.Object, "src");
        var dstRes = new FileSystemResolution(dst.Object, "dst");

        var result = await VfsCopyTool.TransferDirectoryAsync(
            srcRes, dstRes, "/vault/src", "/sandbox/dst",
            overwrite: false, createDirectories: true, deleteSource: false, CancellationToken.None);

        result["status"]!.GetValue<string>().ShouldBe("partial");
        result["summary"]!["transferred"]!.GetValue<int>().ShouldBe(1);
        result["summary"]!["failed"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task TransferDirectoryAsync_GlobEntryNotUnderSourceDir_RecordsFailedAndDoesNotWrite()
    {
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.GlobAsync("src", "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = ["src/a.md", "elsewhere/secret.md"], Truncated = false, Total = 2
            }));
        src.Setup(b => b.ReadChunksAsync("src/a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("A")));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var srcRes = new FileSystemResolution(src.Object, "src");
        var dstRes = new FileSystemResolution(dst.Object, "dst");

        var result = await VfsCopyTool.TransferDirectoryAsync(
            srcRes, dstRes, "/vault/src", "/sandbox/dst",
            overwrite: false, createDirectories: true, deleteSource: false, CancellationToken.None);

        result["status"]!.GetValue<string>().ShouldBe("partial");
        result["summary"]!["transferred"]!.GetValue<int>().ShouldBe(1);
        result["summary"]!["failed"]!.GetValue<int>().ShouldBe(1);

        var failedEntry = result["entries"]!.AsArray()
            .Single(e => e!["status"]!.GetValue<string>() == "failed")!;
        failedEntry["source"]!.GetValue<string>().ShouldBe("elsewhere/secret.md");
        failedEntry["error"]!.GetValue<string>().ShouldContain("not under source directory");
        failedEntry["destination"].ShouldBeNull();

        dst.Verify(b => b.WriteChunksAsync(
                It.Is<string>(p => p.Contains("secret")),
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TransferDirectoryAsync_CrossFsMoveAllSucceed_DeletesSourceRootNotPerFile()
    {
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.GlobAsync("src", "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = ["src/a.md", "src/sub/b.md"], Truncated = false, Total = 2
            }));
        src.Setup(b => b.ReadChunksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("X")));
        src.Setup(b => b.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
            {
                Status = "trashed", Message = "", OriginalPath = "src", TrashPath = ".trash/src"
            }));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var srcRes = new FileSystemResolution(src.Object, "src");
        var dstRes = new FileSystemResolution(dst.Object, "dst");

        await VfsCopyTool.TransferDirectoryAsync(
            srcRes, dstRes, "/vault/src", "/sandbox/dst",
            overwrite: false, createDirectories: true, deleteSource: true, CancellationToken.None);

        src.Verify(b => b.DeleteAsync("src", It.IsAny<CancellationToken>()), Times.Once);
        src.Verify(b => b.DeleteAsync("src/a.md", It.IsAny<CancellationToken>()), Times.Never);
        src.Verify(b => b.DeleteAsync("src/sub/b.md", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TransferDirectoryAsync_CrossFsMovePartialFailure_DoesNotDeleteAnySource()
    {
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.GlobAsync("src", "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = ["src/a.md", "src/b.md"], Truncated = false, Total = 2
            }));
        src.Setup(b => b.ReadChunksAsync("src/a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("A")));
        src.Setup(b => b.ReadChunksAsync("src/b.md", It.IsAny<CancellationToken>()))
            .Throws(new IOException("boom"));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var srcRes = new FileSystemResolution(src.Object, "src");
        var dstRes = new FileSystemResolution(dst.Object, "dst");

        var result = await VfsCopyTool.TransferDirectoryAsync(
            srcRes, dstRes, "/vault/src", "/sandbox/dst",
            overwrite: false, createDirectories: true, deleteSource: true, CancellationToken.None);

        result["status"]!.GetValue<string>().ShouldBe("partial");
        src.Verify(b => b.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TransferDirectoryAsync_TruncatedGlob_FailsWithoutSilentDrop()
    {
        // A capped (file-backed) source glob can't enumerate the whole tree. Copying the partial
        // listing would silently drop files while reporting success, so the transfer must abort.
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.GlobAsync("src", "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = [], Truncated = true, Total = 500
            }));

        var dst = new Mock<IFileSystemBackend>();
        var srcRes = new FileSystemResolution(src.Object, "src");
        var dstRes = new FileSystemResolution(dst.Object, "dst");

        var result = await VfsCopyTool.TransferDirectoryAsync(
            srcRes, dstRes, "/vault/src", "/sandbox/dst",
            overwrite: false, createDirectories: true, deleteSource: true, CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        result["message"]!.GetValue<string>().ShouldContain("500");

        dst.Verify(b => b.WriteChunksAsync(It.IsAny<string>(),
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        src.Verify(b => b.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TransferDirectoryAsync_SkipsDirectoryEntries()
    {
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.GlobAsync("src", "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = ["src/sub/", "src/sub/a.md"], Truncated = false, Total = 2
            }));
        src.Setup(b => b.ReadChunksAsync("src/sub/a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("A")));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var srcRes = new FileSystemResolution(src.Object, "src");
        var dstRes = new FileSystemResolution(dst.Object, "dst");

        var result = await VfsCopyTool.TransferDirectoryAsync(
            srcRes, dstRes, "/vault/src", "/sandbox/dst",
            overwrite: false, createDirectories: true, deleteSource: false, CancellationToken.None);

        result["summary"]!["transferred"]!.GetValue<int>().ShouldBe(1);
        result["summary"]!["failed"]!.GetValue<int>().ShouldBe(0);
        dst.Verify(b => b.WriteChunksAsync("dst/sub/a.md",
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            false, true, It.IsAny<CancellationToken>()), Times.Once);
        dst.Verify(b => b.WriteChunksAsync(It.Is<string>(p => p.EndsWith("/")),
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
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
        src.Setup(b => b.GlobAsync("src", "**/*", VfsGlobMode.Files, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonArray
            {
                new JsonObject { ["path"] = "src/a.md" },
                new JsonObject { ["path"] = "src/sub/b.md" }
            });
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
        src.Setup(b => b.GlobAsync("src", "**/*", VfsGlobMode.Files, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonArray
            {
                new JsonObject { ["path"] = "src/a.md" },
                new JsonObject { ["path"] = "src/b.md" }
            });
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
        src.Setup(b => b.GlobAsync("src", "**/*", VfsGlobMode.Files, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonArray
            {
                new JsonObject { ["path"] = "src/a.md" },
                new JsonObject { ["path"] = "elsewhere/secret.md" }
            });
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
    public async Task TransferDirectoryAsync_MoveOnSuccessfulCopy_DeletesSource()
    {
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.GlobAsync("src", "**/*", VfsGlobMode.Files, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonArray { new JsonObject { ["path"] = "src/a.md" } });
        src.Setup(b => b.ReadChunksAsync("src/a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("A")));

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

        src.Verify(b => b.DeleteAsync("src/a.md", It.IsAny<CancellationToken>()), Times.Once);
    }
}

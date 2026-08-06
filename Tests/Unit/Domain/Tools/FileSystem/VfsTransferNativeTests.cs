using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

// Both ends on one mount, so the backend's own copy or move does the work and decides for itself
// whether the path is a file or a directory — and reports its own not-found. The transfer used to
// probe the source with fs_info before looking at which backends it had, so the common case paid a
// wire round trip whose answer nothing ever read.
public class VfsTransferNativeTests
{
    [Fact]
    public async Task MoveAsync_BothEndsOnOneMount_NeverProbesTheSource()
    {
        var backend = new Mock<IFileSystemBackend>();
        backend.Setup(b => b.MoveAsync("a.md", "b.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsMoveResult>.Ok(new FsMoveResult
            {
                Status = "moved", Message = "", Source = "a.md", Destination = "b.md"
            }));

        var result = await TransferToolDriver.MoveAsync(
            new FileSystemResolution(backend.Object, "a.md"),
            new FileSystemResolution(backend.Object, "b.md"),
            "/vault/a.md", "/vault/b.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        backend.Verify(b => b.InfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CopyAsync_BothEndsOnOneMount_NeverProbesTheSource()
    {
        var backend = new Mock<IFileSystemBackend>();
        backend.Setup(b => b.CopyAsync("a.md", "b.md", false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsCopyResult>.Ok(new FsCopyResult
            {
                Status = "copied", Source = "a.md", Destination = "b.md", Bytes = 5
            }));

        var result = await TransferToolDriver.CopyAsync(
            new FileSystemResolution(backend.Object, "a.md"),
            new FileSystemResolution(backend.Object, "b.md"),
            "/vault/a.md", "/vault/b.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        result["bytes"]!.GetValue<long>().ShouldBe(5);
        backend.Verify(b => b.InfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The probe is what tells a cross-mount transfer whether it is streaming one file or walking a
    // tree, so it stays exactly where it is needed.
    [Fact]
    public async Task CopyAsync_EndsOnTwoMounts_StillProbesTheSource()
    {
        var src = new Mock<IFileSystemBackend>().HoldingFile("a.md");
        src.Setup(b => b.ReadChunksAsync("a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable("hello"u8.ToArray()));
        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync("a.md", It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(5L);

        var result = await TransferToolDriver.CopyAsync(
            new FileSystemResolution(src.Object, "a.md"),
            new FileSystemResolution(dst.Object, "a.md"),
            "/vault/a.md", "/notes/a.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        src.Verify(b => b.InfoAsync("a.md", It.IsAny<CancellationToken>()), Times.Once);
    }
}
using System.Text;
using Domain.Contracts;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsCopyToolCrossFsUnsupportedTests
{
    [Fact]
    public async Task CopyAsync_CrossFsDestinationDoesNotStream_ReturnsUnsupportedOperationEnvelope()
    {
        // A non-disk backend (e.g. /schedules, /ha) throws NotSupportedException from WriteChunksAsync.
        // The cross-mount transfer must surface that as the standard structured envelope, not let the
        // raw exception escape the tool.
        var src = new Mock<IFileSystemBackend>().HoldingFile("a.md");
        src.Setup(b => b.ReadChunksAsync("a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("hello")));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Throws(new NotSupportedException("The schedules filesystem does not support raw byte streaming."));

        var srcRes = new FileSystemResolution(src.Object, "a.md");
        var dstRes = new FileSystemResolution(dst.Object, "a.md");

        var result = await TransferToolDriver.CopyAsync(
            srcRes, dstRes, "/vault/a.md", "/schedules/a.md",
            overwrite: false, createDirectories: true, ct: CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("unsupported_operation");
    }

    [Fact]
    public async Task MoveAsync_CrossFsSourceDoesNotStream_ReturnsUnsupportedAndDoesNotDeleteSource()
    {
        // When the source backend can't stream, a move must not delete the source: nothing was transferred.
        var src = new Mock<IFileSystemBackend>().AllowingMoveOut().HoldingFile("kitchen");
        src.Setup(b => b.ReadChunksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Throws(new NotSupportedException("The Home Assistant filesystem does not support raw byte streaming."));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5L);

        var srcRes = new FileSystemResolution(src.Object, "kitchen");
        var dstRes = new FileSystemResolution(dst.Object, "kitchen.md");

        var result = await TransferToolDriver.MoveAsync(
            srcRes, dstRes, "/ha/kitchen", "/vault/kitchen.md",
            overwrite: false, createDirectories: true, ct: CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("unsupported_operation");
        src.Verify(b => b.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The streaming half of a cross-mount transfer fails for more than an unsupported backend: a
    // disk root answers a missing source with IOException, and fs_info reported exists=false without
    // an error, so the transfer gets that far. The directory path already reports such failures per
    // entry; the single-file path must answer the same envelope instead of throwing out of the tool.
    [Fact]
    public async Task MoveAsync_CrossFsStreamFails_ReturnsErrorEnvelopeAndDoesNotDeleteSource()
    {
        var src = new Mock<IFileSystemBackend>().AllowingMoveOut().HoldingFile("missing.md");
        src.Setup(b => b.ReadChunksAsync("missing.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("hello")));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Throws(new IOException("Path not found: missing.md"));

        var srcRes = new FileSystemResolution(src.Object, "missing.md");
        var dstRes = new FileSystemResolution(dst.Object, "x.md");

        var result = await TransferToolDriver.MoveAsync(
            srcRes, dstRes, "/vault/missing.md", "/sandbox/x.md",
            overwrite: false, createDirectories: true, ct: CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("internal_error");
        result["message"]!.GetValue<string>().ShouldContain("Path not found: missing.md");
        src.Verify(b => b.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Cancellation is the abort it is, never an error envelope the agent's pump would retry.
    [Fact]
    public async Task CopyAsync_CrossFsStreamCancelled_PropagatesTheCancellation()
    {
        var src = new Mock<IFileSystemBackend>().HoldingFile("a.md");
        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Throws(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => TransferToolDriver.CopyAsync(
            new FileSystemResolution(src.Object, "a.md"), new FileSystemResolution(dst.Object, "a.md"),
            "/vault/a.md", "/sandbox/a.md",
            overwrite: false, createDirectories: true, ct: CancellationToken.None));
    }
}
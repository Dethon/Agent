using System.Text;
using Domain.Contracts;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsCopyToolCrossFsUnsupportedTests
{
    [Fact]
    public async Task TransferFileAsync_CrossFsDestinationDoesNotStream_ReturnsUnsupportedOperationEnvelope()
    {
        // A non-disk backend (e.g. /schedules, /ha) throws NotSupportedException from WriteChunksAsync.
        // The cross-mount transfer must surface that as the standard structured envelope, not let the
        // raw exception escape the tool.
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.ReadChunksAsync("a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("hello")));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Throws(new NotSupportedException("The schedules filesystem does not support raw byte streaming."));

        var srcRes = new FileSystemResolution(src.Object, "a.md");
        var dstRes = new FileSystemResolution(dst.Object, "a.md");

        var result = await VfsCopyTool.TransferFileAsync(
            srcRes, dstRes, "/vault/a.md", "/schedules/a.md",
            overwrite: false, createDirectories: true, deleteSource: false, CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("unsupported_operation");
    }

    [Fact]
    public async Task TransferFileAsync_CrossFsSourceDoesNotStream_ReturnsUnsupportedAndDoesNotDeleteSource()
    {
        // When the source backend can't stream, a move must not delete the source: nothing was transferred.
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.ReadChunksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Throws(new NotSupportedException("The Home Assistant filesystem does not support raw byte streaming."));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5L);

        var srcRes = new FileSystemResolution(src.Object, "kitchen");
        var dstRes = new FileSystemResolution(dst.Object, "kitchen.md");

        var result = await VfsCopyTool.TransferFileAsync(
            srcRes, dstRes, "/ha/kitchen", "/vault/kitchen.md",
            overwrite: false, createDirectories: true, deleteSource: true, CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("unsupported_operation");
        src.Verify(b => b.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
using System.Text;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsMoveToolCrossFsTests
{
    [Fact]
    public async Task RunAsync_CrossFsFile_StreamsAndDeletesSource()
    {
        var src = new Mock<IFileSystemBackend>();
        src.SetupGet(b => b.FilesystemName).Returns("vault");
        src.Setup(b => b.InfoAsync("a.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = "a.md", IsDirectory = false }));
        src.Setup(b => b.ReadChunksAsync("a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("hello")));
        src.Setup(b => b.DeleteAsync("a.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
            {
                Status = "trashed", Message = "", OriginalPath = "a.md", TrashPath = ".trash/a.md"
            }));

        var dst = new Mock<IFileSystemBackend>();
        dst.SetupGet(b => b.FilesystemName).Returns("sandbox");
        dst.Setup(b => b.WriteChunksAsync(
                "a.md", It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(5L);

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/vault/a.md"))
            .Returns(new FileSystemResolution(src.Object, "a.md"));
        registry.Setup(r => r.Resolve("/sandbox/a.md"))
            .Returns(new FileSystemResolution(dst.Object, "a.md"));

        var tool = new VfsMoveTool(registry.Object);
        var result = await tool.RunAsync("/vault/a.md", "/sandbox/a.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        dst.Verify(b => b.WriteChunksAsync("a.md",
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            false, true, It.IsAny<CancellationToken>()), Times.Once);
        src.Verify(b => b.DeleteAsync("a.md", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_SameFsFile_StillUsesNativeMoveAsync()
    {
        var backend = new Mock<IFileSystemBackend>();
        backend.SetupGet(b => b.FilesystemName).Returns("vault");
        backend.Setup(b => b.InfoAsync("a.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = "a.md", IsDirectory = false }));
        backend.Setup(b => b.MoveAsync("a.md", "b.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsMoveResult>.Ok(new FsMoveResult
            {
                Status = "moved", Message = "", Source = "a.md", Destination = "b.md"
            }));

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/vault/a.md"))
            .Returns(new FileSystemResolution(backend.Object, "a.md"));
        registry.Setup(r => r.Resolve("/vault/b.md"))
            .Returns(new FileSystemResolution(backend.Object, "b.md"));

        var tool = new VfsMoveTool(registry.Object);
        await tool.RunAsync("/vault/a.md", "/vault/b.md");

        backend.Verify(b => b.MoveAsync("a.md", "b.md", It.IsAny<CancellationToken>()), Times.Once);
        backend.Verify(b => b.ReadChunksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
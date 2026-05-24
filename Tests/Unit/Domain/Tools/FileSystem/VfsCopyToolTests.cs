using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsCopyToolTests
{
    [Fact]
    public async Task RunAsync_SameFsFile_DelegatesToBackendCopyAsync()
    {
        var backend = new Mock<IFileSystemBackend>();
        backend.SetupGet(b => b.FilesystemName).Returns("vault");
        backend.Setup(b => b.InfoAsync("notes/a.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = "notes/a.md", IsDirectory = false }));
        backend.Setup(b => b.CopyAsync("notes/a.md", "notes/b.md", false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsCopyResult>.Ok(new FsCopyResult
            {
                Status = "copied", Source = "notes/a.md", Destination = "notes/b.md", Bytes = 42
            }));

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/vault/notes/a.md"))
            .Returns(new FileSystemResolution(backend.Object, "notes/a.md"));
        registry.Setup(r => r.Resolve("/vault/notes/b.md"))
            .Returns(new FileSystemResolution(backend.Object, "notes/b.md"));

        var tool = new VfsCopyTool(registry.Object);
        var result = await tool.RunAsync("/vault/notes/a.md", "/vault/notes/b.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        result["source"]!.GetValue<string>().ShouldBe("/vault/notes/a.md");
        result["destination"]!.GetValue<string>().ShouldBe("/vault/notes/b.md");
        result["bytes"]!.GetValue<long>().ShouldBe(42L);
        backend.Verify(b => b.CopyAsync("notes/a.md", "notes/b.md", false, true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_CrossFsFile_StreamsThroughAgent()
    {
        var src = new Mock<IFileSystemBackend>();
        src.SetupGet(b => b.FilesystemName).Returns("vault");
        src.Setup(b => b.InfoAsync("a.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = "a.md", IsDirectory = false }));
        src.Setup(b => b.ReadChunksAsync("a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(System.Text.Encoding.UTF8.GetBytes("hello")));

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

        var tool = new VfsCopyTool(registry.Object);
        var result = await tool.RunAsync("/vault/a.md", "/sandbox/a.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        result["bytes"]!.GetValue<long>().ShouldBe(5L);
        dst.Verify(b => b.WriteChunksAsync(
            "a.md", It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            false, true, It.IsAny<CancellationToken>()), Times.Once);
        src.Verify(b => b.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;
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
            .ReturnsAsync(new JsonObject { ["isDirectory"] = false, ["bytes"] = 5 });
        src.Setup(b => b.OpenReadStreamAsync("a.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(Encoding.UTF8.GetBytes("hello")));
        src.Setup(b => b.DeleteAsync("a.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject { ["status"] = "deleted" });

        var dst = new Mock<IFileSystemBackend>();
        dst.SetupGet(b => b.FilesystemName).Returns("sandbox");

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/vault/a.md"))
            .Returns(new FileSystemResolution(src.Object, "a.md"));
        registry.Setup(r => r.Resolve("/sandbox/a.md"))
            .Returns(new FileSystemResolution(dst.Object, "a.md"));

        var tool = new VfsMoveTool(registry.Object);
        var result = await tool.RunAsync("/vault/a.md", "/sandbox/a.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        dst.Verify(b => b.WriteFromStreamAsync("a.md", It.IsAny<Stream>(),
            false, true, It.IsAny<CancellationToken>()), Times.Once);
        src.Verify(b => b.DeleteAsync("a.md", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_SameFsFile_StillUsesNativeMoveAsync()
    {
        var backend = new Mock<IFileSystemBackend>();
        backend.SetupGet(b => b.FilesystemName).Returns("vault");
        backend.Setup(b => b.InfoAsync("a.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject { ["isDirectory"] = false });
        backend.Setup(b => b.MoveAsync("a.md", "b.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject { ["status"] = "moved" });

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/vault/a.md"))
            .Returns(new FileSystemResolution(backend.Object, "a.md"));
        registry.Setup(r => r.Resolve("/vault/b.md"))
            .Returns(new FileSystemResolution(backend.Object, "b.md"));

        var tool = new VfsMoveTool(registry.Object);
        await tool.RunAsync("/vault/a.md", "/vault/b.md");

        backend.Verify(b => b.MoveAsync("a.md", "b.md", It.IsAny<CancellationToken>()), Times.Once);
        backend.Verify(b => b.OpenReadStreamAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

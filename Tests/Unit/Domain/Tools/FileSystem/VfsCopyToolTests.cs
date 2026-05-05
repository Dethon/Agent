using System.Text.Json.Nodes;
using Domain.Contracts;
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
            .ReturnsAsync(new JsonObject { ["type"] = "file", ["bytes"] = 42 });
        backend.Setup(b => b.CopyAsync("notes/a.md", "notes/b.md", false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject { ["status"] = "copied", ["bytes"] = 42 });

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
        backend.Verify(b => b.CopyAsync("notes/a.md", "notes/b.md", false, true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_CrossFsFile_StreamsThroughAgent()
    {
        var src = new Mock<IFileSystemBackend>();
        src.SetupGet(b => b.FilesystemName).Returns("vault");
        src.Setup(b => b.InfoAsync("a.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject { ["type"] = "file", ["bytes"] = 5 });
        src.Setup(b => b.OpenReadStreamAsync("a.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello")));

        var dst = new Mock<IFileSystemBackend>();
        dst.SetupGet(b => b.FilesystemName).Returns("sandbox");

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/vault/a.md"))
            .Returns(new FileSystemResolution(src.Object, "a.md"));
        registry.Setup(r => r.Resolve("/sandbox/a.md"))
            .Returns(new FileSystemResolution(dst.Object, "a.md"));

        var tool = new VfsCopyTool(registry.Object);
        var result = await tool.RunAsync("/vault/a.md", "/sandbox/a.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        dst.Verify(b => b.WriteFromStreamAsync(
            "a.md", It.IsAny<Stream>(), false, true, It.IsAny<CancellationToken>()), Times.Once);
        src.Verify(b => b.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

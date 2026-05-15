using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class MoveToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsMoveTool _tool;

    public MoveToolTests()
    {
        _tool = new VfsMoveTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_SameFilesystem_ResolvesAndCallsBackend()
    {
        _registry.Setup(r => r.Resolve("/library/old/file.md"))
            .Returns(new FileSystemResolution(_backend.Object, "old/file.md"));
        _registry.Setup(r => r.Resolve("/library/new/file.md"))
            .Returns(new FileSystemResolution(_backend.Object, "new/file.md"));
        _backend.Setup(b => b.InfoAsync("old/file.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject { ["type"] = "file" });
        _backend.Setup(b => b.MoveAsync("old/file.md", "new/file.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject { ["status"] = "moved" });

        var result = await _tool.RunAsync("/library/old/file.md", "/library/new/file.md", cancellationToken: CancellationToken.None);

        result["status"]!.GetValue<string>().ShouldBe("ok");
        _backend.Verify(b => b.MoveAsync("old/file.md", "new/file.md", It.IsAny<CancellationToken>()), Times.Once);
    }
}
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
    private readonly MoveTool _tool;

    public MoveToolTests()
    {
        _tool = new MoveTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_SameFilesystem_ResolvesAndCallsBackend()
    {
        var expected = new JsonObject { ["status"] = "success" };
        _registry.Setup(r => r.Resolve("/library/old/file.md"))
            .Returns(new FileSystemResolution(_backend.Object, "old/file.md"));
        _registry.Setup(r => r.Resolve("/library/new/file.md"))
            .Returns(new FileSystemResolution(_backend.Object, "new/file.md"));
        _backend.Setup(b => b.MoveAsync("old/file.md", "new/file.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/library/old/file.md", "/library/new/file.md", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_DifferentFilesystems_ThrowsClearError()
    {
        var backend2 = new Mock<IFileSystemBackend>().Object;
        _registry.Setup(r => r.Resolve("/library/file.md"))
            .Returns(new FileSystemResolution(_backend.Object, "file.md"));
        _registry.Setup(r => r.Resolve("/vault/file.md"))
            .Returns(new FileSystemResolution(backend2, "file.md"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _tool.RunAsync("/library/file.md", "/vault/file.md", cancellationToken: CancellationToken.None));
    }
}

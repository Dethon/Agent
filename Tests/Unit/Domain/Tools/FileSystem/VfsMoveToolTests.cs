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
    public async Task RunAsync_DifferentFilesystems_ReturnsCrossFilesystemError()
    {
        var backend2 = new Mock<IFileSystemBackend>();
        backend2.Setup(b => b.FilesystemName).Returns("vault");
        _backend.Setup(b => b.FilesystemName).Returns("library");
        _registry.Setup(r => r.Resolve("/library/file.md"))
            .Returns(new FileSystemResolution(_backend.Object, "file.md"));
        _registry.Setup(r => r.Resolve("/vault/file.md"))
            .Returns(new FileSystemResolution(backend2.Object, "file.md"));

        var result = await _tool.RunAsync("/library/file.md", "/vault/file.md", cancellationToken: CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("cross_filesystem");
        result["message"]!.GetValue<string>().ShouldContain("Cannot move between different filesystems");
    }
}

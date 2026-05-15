using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class TextReadToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsTextReadTool _tool;

    public TextReadToolTests()
    {
        _tool = new VfsTextReadTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndCallsBackend()
    {
        var expected = new JsonObject { ["content"] = "1: hello", ["totalLines"] = 1, ["truncated"] = false };
        _registry.Setup(r => r.Resolve("/library/notes/todo.md"))
            .Returns(new FileSystemResolution(_backend.Object, "notes/todo.md"));
        _backend.Setup(b => b.ReadAsync("notes/todo.md", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/library/notes/todo.md", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_PassesOffsetAndLimit()
    {
        var expected = new JsonObject { ["content"] = "10: line", ["totalLines"] = 100, ["truncated"] = true };
        _registry.Setup(r => r.Resolve("/vault/data.md"))
            .Returns(new FileSystemResolution(_backend.Object, "data.md"));
        _backend.Setup(b => b.ReadAsync("data.md", 10, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/vault/data.md", offset: 10, limit: 50, cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_UnknownMount_ThrowsFromRegistry()
    {
        _registry.Setup(r => r.Resolve("/unknown/file.md"))
            .Throws(new InvalidOperationException("No filesystem mounted"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _tool.RunAsync("/unknown/file.md", cancellationToken: CancellationToken.None));
    }
}
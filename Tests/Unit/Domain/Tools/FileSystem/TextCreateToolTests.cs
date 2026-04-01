using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class TextCreateToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly TextCreateTool _tool;

    public TextCreateToolTests()
    {
        _tool = new TextCreateTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndCallsBackend()
    {
        var expected = new JsonObject { ["status"] = "created", ["path"] = "notes/new.md" };
        _registry.Setup(r => r.Resolve("/library/notes/new.md"))
            .Returns(new FileSystemResolution(_backend.Object, "notes/new.md"));
        _backend.Setup(b => b.CreateAsync("notes/new.md", "# Hello", false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/library/notes/new.md", "# Hello", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_PassesOverwriteAndCreateDirectories()
    {
        var expected = new JsonObject { ["status"] = "created" };
        _registry.Setup(r => r.Resolve("/vault/data.json"))
            .Returns(new FileSystemResolution(_backend.Object, "data.json"));
        _backend.Setup(b => b.CreateAsync("data.json", "{}", true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/vault/data.json", "{}", overwrite: true, createDirectories: false, cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }
}

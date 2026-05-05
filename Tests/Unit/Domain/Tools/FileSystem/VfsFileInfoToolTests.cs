using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsFileInfoToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsFileInfoTool _tool;

    public VfsFileInfoToolTests()
    {
        _tool = new VfsFileInfoTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndCallsBackend()
    {
        var expected = new JsonObject
        {
            ["exists"] = true,
            ["isDirectory"] = false,
            ["size"] = 1234,
            ["lastModified"] = "2026-05-05T12:34:56Z"
        };
        _registry.Setup(r => r.Resolve("/library/notes/todo.md"))
            .Returns(new FileSystemResolution(_backend.Object, "notes/todo.md"));
        _backend.Setup(b => b.InfoAsync("notes/todo.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/library/notes/todo.md", CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_NonExistentPath_ReturnsBackendResult()
    {
        var expected = new JsonObject { ["exists"] = false };
        _registry.Setup(r => r.Resolve("/vault/missing.md"))
            .Returns(new FileSystemResolution(_backend.Object, "missing.md"));
        _backend.Setup(b => b.InfoAsync("missing.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/vault/missing.md", CancellationToken.None);

        result.ShouldBe(expected);
    }
}

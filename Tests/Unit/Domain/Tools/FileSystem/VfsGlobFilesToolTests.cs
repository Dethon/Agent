using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class GlobFilesToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsGlobFilesTool _tool;

    public GlobFilesToolTests()
    {
        _tool = new VfsGlobFilesTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesBasePathAndCallsBackend()
    {
        var expected = new JsonObject { ["files"] = new JsonArray("a.md", "b.md") };
        _registry.Setup(r => r.Resolve("/library"))
            .Returns(new FileSystemResolution(_backend.Object, ""));
        _backend.Setup(b => b.GlobAsync("", "**/*.md", VfsGlobMode.Files, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/library", "**/*.md", VfsGlobMode.Files, cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_WithSubdirectory_ResolvesRelativePath()
    {
        var expected = new JsonObject { ["directories"] = new JsonObject() };
        _registry.Setup(r => r.Resolve("/vault/docs"))
            .Returns(new FileSystemResolution(_backend.Object, "docs"));
        _backend.Setup(b => b.GlobAsync("docs", "*", VfsGlobMode.Directories, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/vault/docs", "*", VfsGlobMode.Directories, cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }
}

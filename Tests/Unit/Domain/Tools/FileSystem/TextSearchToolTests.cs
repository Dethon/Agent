using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class TextSearchToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly TextSearchTool _tool;

    public TextSearchToolTests()
    {
        _tool = new TextSearchTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_DirectorySearch_ResolvesAndCallsBackend()
    {
        var expected = new JsonObject { ["totalMatches"] = 5 };
        _registry.Setup(r => r.Resolve("/library"))
            .Returns(new FileSystemResolution(_backend.Object, ""));
        _backend.Setup(b => b.SearchAsync("kubernetes", false, null, "", null, 50, 1, "content", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("kubernetes", directoryPath: "/library", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_SingleFileSearch_ResolvesFilePath()
    {
        var expected = new JsonObject { ["totalMatches"] = 1 };
        _registry.Setup(r => r.Resolve("/vault/notes/todo.md"))
            .Returns(new FileSystemResolution(_backend.Object, "notes/todo.md"));
        _backend.Setup(b => b.SearchAsync("TODO", false, "notes/todo.md", null, null, 50, 1, "content", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("TODO", filePath: "/vault/notes/todo.md", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_NeitherFilePathNorDirectoryPath_ThrowsArgumentException()
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _tool.RunAsync("query", cancellationToken: CancellationToken.None));
    }
}

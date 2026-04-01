using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class TextEditToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly TextEditTool _tool;

    public TextEditToolTests()
    {
        _tool = new TextEditTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndCallsBackend()
    {
        var expected = new JsonObject { ["status"] = "success", ["occurrencesReplaced"] = 1 };
        _registry.Setup(r => r.Resolve("/library/file.md"))
            .Returns(new FileSystemResolution(_backend.Object, "file.md"));
        _backend.Setup(b => b.EditAsync("file.md", "old", "new", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/library/file.md", "old", "new", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_PassesReplaceAll()
    {
        var expected = new JsonObject { ["status"] = "success", ["occurrencesReplaced"] = 3 };
        _registry.Setup(r => r.Resolve("/vault/config.md"))
            .Returns(new FileSystemResolution(_backend.Object, "config.md"));
        _backend.Setup(b => b.EditAsync("config.md", "foo", "bar", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/vault/config.md", "foo", "bar", replaceAll: true, cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }
}

using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class ListToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly ListTool _tool;

    public ListToolTests()
    {
        _tool = new ListTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndCallsBackend()
    {
        var expected = new JsonObject { ["path"] = "docs", ["entries"] = new JsonArray() };
        _registry.Setup(r => r.Resolve("/library/docs"))
            .Returns(new FileSystemResolution(_backend.Object, "docs"));
        _backend.Setup(b => b.ListAsync("docs", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/library/docs", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_RootPath_PassesEmptyRelativePath()
    {
        var expected = new JsonObject { ["path"] = "", ["entries"] = new JsonArray() };
        _registry.Setup(r => r.Resolve("/vault"))
            .Returns(new FileSystemResolution(_backend.Object, ""));
        _backend.Setup(b => b.ListAsync("", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/vault", cancellationToken: CancellationToken.None);

        result.ShouldBe(expected);
    }
}

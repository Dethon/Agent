using Domain.Contracts;
using Domain.DTOs.FileSystem;
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
        _registry.Setup(r => r.Resolve("/library/notes/todo.md"))
            .Returns(new FileSystemResolution(_backend.Object, "notes/todo.md"));
        _backend.Setup(b => b.ReadAsync("notes/todo.md", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsReadResult>.Ok(new FsReadResult
            {
                FilePath = "notes/todo.md", Content = "1: hello", TotalLines = 1, Truncated = false
            }));

        var result = await _tool.RunAsync("/library/notes/todo.md", cancellationToken: CancellationToken.None);

        result!["content"]!.GetValue<string>().ShouldBe("1: hello");
        result["totalLines"]!.GetValue<int>().ShouldBe(1);
        result["truncated"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_PassesOffsetAndLimit()
    {
        _registry.Setup(r => r.Resolve("/vault/data.md"))
            .Returns(new FileSystemResolution(_backend.Object, "data.md"));
        _backend.Setup(b => b.ReadAsync("data.md", 10, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsReadResult>.Ok(new FsReadResult
            {
                FilePath = "data.md", Content = "10: line", TotalLines = 100, Truncated = true
            }));

        var result = await _tool.RunAsync("/vault/data.md", offset: 10, limit: 50, cancellationToken: CancellationToken.None);

        result!["content"]!.GetValue<string>().ShouldBe("10: line");
        result["truncated"]!.GetValue<bool>().ShouldBeTrue();
        _backend.Verify(b => b.ReadAsync("data.md", 10, 50, It.IsAny<CancellationToken>()), Times.Once);
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
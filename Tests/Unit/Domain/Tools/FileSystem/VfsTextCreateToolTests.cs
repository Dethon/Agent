using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class TextCreateToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsTextCreateTool _tool;

    public TextCreateToolTests()
    {
        _tool = new VfsTextCreateTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndCallsBackend()
    {
        _registry.Setup(r => r.Resolve("/library/notes/new.md"))
            .Returns(new FileSystemResolution(_backend.Object, "notes/new.md"));
        _backend.Setup(b => b.CreateAsync("notes/new.md", "# Hello", false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsCreateResult>.Ok(new FsCreateResult
            {
                Status = "created", FilePath = "notes/new.md", Size = "7", Lines = 1
            }));

        var result = await _tool.RunAsync("/library/notes/new.md", "# Hello", cancellationToken: CancellationToken.None);

        result!["status"]!.GetValue<string>().ShouldBe("created");
        result["filePath"]!.GetValue<string>().ShouldBe("notes/new.md");
    }

    [Fact]
    public async Task RunAsync_PassesOverwriteAndCreateDirectories()
    {
        _registry.Setup(r => r.Resolve("/vault/data.json"))
            .Returns(new FileSystemResolution(_backend.Object, "data.json"));
        _backend.Setup(b => b.CreateAsync("data.json", "{}", true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsCreateResult>.Ok(new FsCreateResult
            {
                Status = "created", FilePath = "data.json", Size = "2", Lines = 1
            }));

        var result = await _tool.RunAsync("/vault/data.json", "{}", overwrite: true, createDirectories: false, cancellationToken: CancellationToken.None);

        result!["status"]!.GetValue<string>().ShouldBe("created");
        _backend.Verify(b => b.CreateAsync("data.json", "{}", true, false, It.IsAny<CancellationToken>()), Times.Once);
    }
}
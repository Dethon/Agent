using Domain.Contracts;
using Domain.DTOs.FileSystem;
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
        _registry.Setup(r => r.Resolve("/library"))
            .Returns(new FileSystemResolution(_backend.Object, ""));
        _backend.Setup(b => b.GlobAsync("", "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = ["a.md", "sub/"], Truncated = false, Total = 2
            }));

        var result = await _tool.RunAsync("/library", "**/*", cancellationToken: CancellationToken.None);

        result!["entries"]!.AsArray().Count.ShouldBe(2);
        result["total"]!.GetValue<int>().ShouldBe(2);
        result["truncated"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_WithSubdirectory_ResolvesRelativePath()
    {
        _registry.Setup(r => r.Resolve("/vault/docs"))
            .Returns(new FileSystemResolution(_backend.Object, "docs"));
        _backend.Setup(b => b.GlobAsync("docs", "*/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = ["docs/"], Truncated = false, Total = 1
            }));

        var result = await _tool.RunAsync("/vault/docs", "*/", cancellationToken: CancellationToken.None);

        result!["entries"]!.AsArray().Count.ShouldBe(1);
        result["total"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task Name_IsGlob()
    {
        VfsGlobFilesTool.Name.ShouldBe("glob");
        VfsGlobFilesTool.Key.ShouldBe("glob");
    }
}
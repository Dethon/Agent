using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class MoveToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsMoveTool _tool;

    public MoveToolTests()
    {
        _tool = new VfsMoveTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_SameFilesystem_ResolvesAndCallsBackend()
    {
        _registry.Setup(r => r.Resolve("/library/old/file.md"))
            .Returns(new FileSystemResolution(_backend.Object, "old/file.md"));
        _registry.Setup(r => r.Resolve("/library/new/file.md"))
            .Returns(new FileSystemResolution(_backend.Object, "new/file.md"));
        _backend.Setup(b => b.InfoAsync("old/file.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = "old/file.md", IsDirectory = false }));
        _backend.Setup(b => b.MoveAsync("old/file.md", "new/file.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsMoveResult>.Ok(new FsMoveResult
            {
                Status = "moved", Message = "", Source = "old/file.md", Destination = "new/file.md"
            }));

        var result = await _tool.RunAsync("/library/old/file.md", "/library/new/file.md", cancellationToken: CancellationToken.None);

        result["status"]!.GetValue<string>().ShouldBe("ok");
        _backend.Verify(b => b.MoveAsync("old/file.md", "new/file.md", It.IsAny<CancellationToken>()), Times.Once);
    }
}
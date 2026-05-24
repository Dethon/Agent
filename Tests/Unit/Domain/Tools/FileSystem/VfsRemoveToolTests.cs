using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class RemoveToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsRemoveTool _tool;

    public RemoveToolTests()
    {
        _tool = new VfsRemoveTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndCallsBackend()
    {
        _registry.Setup(r => r.Resolve("/library/old/file.md"))
            .Returns(new FileSystemResolution(_backend.Object, "old/file.md"));
        _backend.Setup(b => b.DeleteAsync("old/file.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
            {
                Status = "trashed", Message = "", OriginalPath = "old/file.md", TrashPath = ".trash/file.md"
            }));

        var result = await _tool.RunAsync("/library/old/file.md", cancellationToken: CancellationToken.None);

        result!["status"]!.GetValue<string>().ShouldBe("trashed");
        result["trashPath"]!.GetValue<string>().ShouldBe(".trash/file.md");
    }
}
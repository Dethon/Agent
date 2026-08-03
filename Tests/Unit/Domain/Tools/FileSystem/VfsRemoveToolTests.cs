using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsRemoveToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsRemoveTool _tool;

    public VfsRemoveToolTests()
    {
        _tool = new VfsRemoveTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndReturnsTheRemoval()
    {
        _registry.Setup(r => r.Resolve("/library/old.pdf"))
            .Returns(Resolved(_backend.Object, "old.pdf"));
        _backend.Setup(b => b.DeleteAsync("old.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
            {
                Status = "success", Message = "Moved to trash", OriginalPath = "old.pdf", TrashPath = ".trash/old.pdf"
            }));

        var result = await _tool.RunAsync("/library/old.pdf");

        result!["status"]!.GetValue<string>().ShouldBe("success");
        result["trashPath"]!.GetValue<string>().ShouldBe(".trash/old.pdf");
        _backend.Verify(b => b.DeleteAsync("old.pdf", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_MissingPath_ReturnsTheBackendsErrorEnvelope()
    {
        _registry.Setup(r => r.Resolve("/library/missing.pdf"))
            .Returns(Resolved(_backend.Object, "missing.pdf"));
        _backend.Setup(b => b.DeleteAsync("missing.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsRemoveResult>.Err(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.NotFound, Message = "Path not found: missing.pdf", Retryable = false
            }));

        var result = await _tool.RunAsync("/library/missing.pdf");

        result!["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.NotFound);
    }

    private static FsResult<FileSystemResolution> Resolved(IFileSystemBackend backend, string relativePath) =>
        new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(backend, relativePath));
}
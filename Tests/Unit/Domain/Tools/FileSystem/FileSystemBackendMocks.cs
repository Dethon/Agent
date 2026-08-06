using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Moq;

namespace Tests.Unit.Domain.Tools.FileSystem;

// Every cross-mount move asks its source whether the path may leave before it streams, so a mocked
// source has to answer. Real backends answer from the base default without a line of code; a mock
// has no base, so it says the same thing here.
internal static class FileSystemBackendMocks
{
    public static Mock<IFileSystemBackend> AllowingMoveOut(this Mock<IFileSystemBackend> backend)
    {
        backend.Setup(b => b.MoveOutCheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) => FsMoveOutCheckResult.Allow(path));
        return backend;
    }

    public static Mock<IFileSystemBackend> RefusingMoveOut(
        this Mock<IFileSystemBackend> backend, string message, string? hint = null)
    {
        backend.Setup(b => b.MoveOutCheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FsError.Fail<FsMoveOutCheckResult>(
                ToolError.Codes.UnsupportedOperation, message, retryable: false, hint));
        return backend;
    }
}
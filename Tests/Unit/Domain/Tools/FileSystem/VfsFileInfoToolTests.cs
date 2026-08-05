using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsFileInfoToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsFileInfoTool _tool;

    public VfsFileInfoToolTests()
    {
        _tool = new VfsFileInfoTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndCallsBackend()
    {
        _registry.Setup(r => r.Resolve("/library/notes/todo.md"))
            .Returns(Resolved(_backend.Object, "notes/todo.md"));
        _backend.Setup(b => b.InfoAsync("notes/todo.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsInfoResult>.Ok(new FsInfoResult
            {
                Exists = true, Path = "notes/todo.md", IsDirectory = false, Size = 1234, LastModified = "2026-05-05T12:34:56Z"
            }));

        var result = await _tool.RunAsync("/library/notes/todo.md", CancellationToken.None);

        result!["exists"]!.GetValue<bool>().ShouldBeTrue();
        result["isDirectory"]!.GetValue<bool>().ShouldBeFalse();
        result["size"]!.GetValue<long>().ShouldBe(1234);
    }

    [Fact]
    public async Task RunAsync_NonExistentPath_ReturnsBackendResult()
    {
        _registry.Setup(r => r.Resolve("/vault/missing.md"))
            .Returns(Resolved(_backend.Object, "missing.md"));
        _backend.Setup(b => b.InfoAsync("missing.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsInfoResult>.Ok(new FsInfoResult
            {
                Exists = false, Path = "missing.md"
            }));

        var result = await _tool.RunAsync("/vault/missing.md", CancellationToken.None);

        result!["exists"]!.GetValue<bool>().ShouldBeFalse();
    }

    // A backend reports the path in its own coordinates — a disk root even reports the
    // container-absolute one ('/home/x' on a mount whose root is '/'), which the registry would
    // refuse if reused. The Vfs layer answers the full virtual path, like glob does.
    [Fact]
    public async Task RunAsync_BackendLocalPath_IsReplacedWithTheFullVirtualPath()
    {
        _registry.Setup(r => r.Resolve("/sandbox/home/x"))
            .Returns(Resolved(_backend.Object, "home/x", "/sandbox"));
        _backend.Setup(b => b.InfoAsync("home/x", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsInfoResult>.Ok(new FsInfoResult
            {
                Exists = true, Path = "/home/x", IsDirectory = false, Size = 1
            }));

        var result = await _tool.RunAsync("/sandbox/home/x", CancellationToken.None);

        result!["path"]!.GetValue<string>().ShouldBe("/sandbox/home/x");
    }

    private static FsResult<FileSystemResolution> Resolved(
        IFileSystemBackend backend, string relativePath, string mountPoint = "") =>
        new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(backend, relativePath, mountPoint));

}
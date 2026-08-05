using System.Text;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

// A streamed cross-mount move is copy + delete. A backend may refuse the delete (the media
// mount's DownloadsOverlay refuses paths outside downloads/<id>) — reporting "ok" then would
// present a duplicate-leaving copy as a completed move.
public class VfsTransferDeleteSourceTests
{
    [Fact]
    public async Task TransferFileAsync_SourceDeleteRefused_ReportsTheFailureNotOk()
    {
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.ReadChunksAsync("movies/a.mkv", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("A")));
        src.Setup(b => b.DeleteAsync("movies/a.mkv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FsError.Fail<FsRemoveResult>(
                ToolError.Codes.UnsupportedOperation, "delete refused by the media filesystem"));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var result = await VfsCopyTool.TransferFileAsync(
            new FileSystemResolution(src.Object, "movies/a.mkv"),
            new FileSystemResolution(dst.Object, "a.mkv"),
            "/media/movies/a.mkv", "/vault/a.mkv",
            overwrite: false, createDirectories: true, deleteSource: true, CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.UnsupportedOperation);
        result["message"]!.GetValue<string>().ShouldContain("/vault/a.mkv");
        result["message"]!.GetValue<string>().ShouldContain("delete refused by the media filesystem");
        result["hint"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }

    // A cross-mount move of a directory with nothing to stream used to answer "ok" while doing
    // nothing: no destination was created, and the skipped delete left the source in place.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferDirectoryAsync_NothingToStreamOnAMove_ReportsTheRefusalNotOk(bool onlyDirMarkers)
    {
        string[] entries = onlyDirMarkers ? ["src/sub/"] : [];
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.GlobAsync("src", "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = entries, Truncated = false, Total = entries.Length
            }));
        var dst = new Mock<IFileSystemBackend>();

        var result = await VfsCopyTool.TransferDirectoryAsync(
            new FileSystemResolution(src.Object, "src"),
            new FileSystemResolution(dst.Object, "dst"),
            "/media/src", "/vault/dst",
            overwrite: false, createDirectories: true, deleteSource: true, CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.UnsupportedOperation);
        result["message"]!.GetValue<string>().ShouldContain("/media/src");
        src.Verify(b => b.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TransferDirectoryAsync_SourceDeleteRefused_ReportsTheFailureNotOk()
    {
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.GlobAsync("src", "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = ["src/a.md"], Truncated = false, Total = 1
            }));
        src.Setup(b => b.ReadChunksAsync("src/a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("A")));
        src.Setup(b => b.DeleteAsync("src", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FsError.Fail<FsRemoveResult>(
                ToolError.Codes.UnsupportedOperation, "delete refused by the media filesystem"));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var result = await VfsCopyTool.TransferDirectoryAsync(
            new FileSystemResolution(src.Object, "src"),
            new FileSystemResolution(dst.Object, "dst"),
            "/media/src", "/vault/dst",
            overwrite: false, createDirectories: true, deleteSource: true, CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.UnsupportedOperation);
        result["message"]!.GetValue<string>().ShouldContain("/vault/dst");
        result["message"]!.GetValue<string>().ShouldContain("delete refused by the media filesystem");
        result["hint"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }
}
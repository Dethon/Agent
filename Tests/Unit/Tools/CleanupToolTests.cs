using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools;
using Moq;
using Shouldly;

namespace Tests.Unit.Tools;

public class CleanupToolTests
{
    private readonly Mock<IDownloadClient> _mockDownloadClient;
    private readonly Mock<IFileSystemClient> _mockFileSystemClient;
    private readonly string _baseDownloadLocation;
    private readonly CleanupTool _cleanupTool;

    public CleanupToolTests()
    {
        _mockDownloadClient = new Mock<IDownloadClient>();
        _mockFileSystemClient = new Mock<IFileSystemClient>();
        _baseDownloadLocation = "/downloads";
        _cleanupTool = new CleanupTool(
            _mockDownloadClient.Object,
            _mockFileSystemClient.Object,
            _baseDownloadLocation);
    }

    [Fact]
    public async Task Run_ShouldCleanupFilesAndReturnSuccessResponse()
    {
        // given
        const int downloadId = 123;
        var parameters = new JsonObject
        {
            ["DownloadId"] = downloadId
        };

        SetupSuccessfulFileSystemRemoval(downloadId);
        SetupSuccessfulDownloadCleanup(downloadId);

        // when
        var result = await _cleanupTool.Run(parameters);

        // then
        VerifyFileSystemRemovalCalled(downloadId, Times.Once());
        VerifyDownloadCleanupCalled(downloadId, Times.Once());

        VerifySuccessResponse(result, downloadId);
    }

    [Fact]
    public async Task Run_WhenFileSystemClientThrowsException_ShouldPropagateException()
    {
        // given
        const int downloadId = 123;
        var parameters = new JsonObject
        {
            ["DownloadId"] = downloadId
        };

        var expectedException = new InvalidOperationException("Directory removal failed");
        SetupFailingFileSystemRemoval(expectedException);

        // when/then
        var exception = await Should.ThrowAsync<InvalidOperationException>(() => _cleanupTool.Run(parameters));

        exception.Message.ShouldBe(expectedException.Message);
        VerifyDownloadCleanupCalled(downloadId, Times.Never());
    }

    [Fact]
    public async Task Run_WhenDownloadClientThrowsException_ShouldPropagateException()
    {
        // given
        const int downloadId = 123;
        var parameters = new JsonObject
        {
            ["DownloadId"] = downloadId
        };

        SetupSuccessfulFileSystemRemoval(downloadId);

        var expectedException = new InvalidOperationException("Download cleanup failed");
        SetupFailingDownloadCleanup(expectedException);

        // when/then
        var exception = await Should.ThrowAsync<InvalidOperationException>(() => _cleanupTool.Run(parameters));

        exception.Message.ShouldBe(expectedException.Message);
        VerifyFileSystemRemovalCalled(downloadId, Times.Once());
    }

    [Fact]
    public async Task Run_WithInvalidParameters_ShouldThrowException()
    {
        // given
        var parameters = new JsonObject(); // Missing required DownloadId

        // when/then
        await Should.ThrowAsync<ArgumentException>(() => _cleanupTool.Run(parameters));
    }

    [Fact]
    public async Task Run_WithNullParameters_ShouldThrowException()
    {
        // given
        JsonNode? parameters = null;

        // when/then
        await Should.ThrowAsync<ArgumentNullException>(() => _cleanupTool.Run(parameters));
    }

    #region Helper Methods

    private void SetupSuccessfulFileSystemRemoval(int downloadId)
    {
        _mockFileSystemClient
            .Setup(x => x.RemoveDirectory($"{_baseDownloadLocation}/{downloadId}", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private void SetupFailingFileSystemRemoval(Exception exception)
    {
        _mockFileSystemClient
            .Setup(x => x.RemoveDirectory(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);
    }

    private void SetupSuccessfulDownloadCleanup(int downloadId)
    {
        _mockDownloadClient
            .Setup(x => x.Cleanup($"{downloadId}", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private void SetupFailingDownloadCleanup(Exception exception)
    {
        _mockDownloadClient
            .Setup(x => x.Cleanup(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);
    }

    private void VerifyFileSystemRemovalCalled(int downloadId, Times times)
    {
        _mockFileSystemClient.Verify(
            x => x.RemoveDirectory($"{_baseDownloadLocation}/{downloadId}", It.IsAny<CancellationToken>()),
            times);
    }

    private void VerifyDownloadCleanupCalled(int downloadId, Times times)
    {
        _mockDownloadClient.Verify(
            x => x.Cleanup($"{downloadId}", It.IsAny<CancellationToken>()),
            times);
    }

    private static void VerifySuccessResponse(JsonNode result, int downloadId)
    {
        result["status"]?.GetValue<string>().ShouldBe("success");
        result["message"]?.GetValue<string>().ShouldBe("Download leftovers removed successfully");
        result["downloadId"]?.GetValue<int>().ShouldBe(downloadId);
    }

    #endregion
}
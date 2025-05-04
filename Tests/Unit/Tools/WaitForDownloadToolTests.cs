using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools;
using Moq;
using Shouldly;

namespace Tests.Unit.Tools;

public class WaitForDownloadToolTests
{
    private readonly Mock<IDownloadClient> _mockClient;
    private readonly WaitForDownloadTool _tool;

    public WaitForDownloadToolTests()
    {
        _mockClient = new Mock<IDownloadClient>();
        _tool = new WaitForDownloadTool(_mockClient.Object);
    }

    [Fact]
    public async Task Run_ShouldWaitForDownloadCompletion_AndReturnSuccess()
    {
        // given
        const int downloadId = 123;
        var parameters = new JsonObject
        {
            ["DownloadId"] = downloadId
        };

        var downloadItem = CreateDownloadItem(downloadId, "Test Download", DownloadStatus.InProgress);
        var completedItem = CreateDownloadItem(downloadId, "Test Download", DownloadStatus.Completed);

        SetupMockClientForInProgressThenCompleted(downloadId, downloadItem, completedItem);

        // when
        var result = await _tool.Run(parameters);

        // then
        AssertSuccessResult(result, downloadId);
        VerifyGetDownloadItemCalled(downloadId, Times.Once());
    }

    [Fact]
    public async Task Run_ShouldStopWaiting_WhenCancellationRequested()
    {
        // given
        const int downloadId = 456;
        var parameters = new JsonObject
        {
            ["DownloadId"] = downloadId
        };

        var downloadItem = CreateDownloadItem(downloadId, "Cancelled Download", DownloadStatus.InProgress);
        var cancellationTokenSource = new CancellationTokenSource();

        SetupMockClientForCancellation(downloadId, downloadItem, cancellationTokenSource);

        // when
        await Should.ThrowAsync<TaskCanceledException>(async () =>
        {
            await _tool.Run(parameters, cancellationTokenSource.Token);
        });

        // then
        VerifyGetDownloadItemCalled(downloadId, Times.Once());
    }

    [Fact]
    public async Task Run_ShouldHandleImmediateCompletion()
    {
        // given
        const int downloadId = 789;
        var parameters = new JsonObject
        {
            ["DownloadId"] = downloadId
        };

        var completedItem = CreateDownloadItem(downloadId, "Completed Download", DownloadStatus.Completed);

        SetupMockClientForCompletedDownload(downloadId, completedItem);

        // when
        var result = await _tool.Run(parameters);

        // then
        AssertSuccessResult(result, downloadId);
        VerifyGetDownloadItemCalled(downloadId, Times.Once());
    }

    [Fact]
    public void GetToolDefinition_ShouldReturnCorrectDefinition()
    {
        // given/when
        var definition = _tool.GetToolDefinition();

        // then
        definition.ShouldBeOfType<ToolDefinition<WaitForDownloadParams>>();
        definition.Name.ShouldBe("WaitForDownload");
        definition.Description.ShouldContain("Monitors a download");
    }

    [Fact]
    public async Task Run_ShouldThrowException_WhenParametersAreInvalid()
    {
        // given
        var invalidParams = new JsonObject
        {
            ["InvalidProperty"] = "value"
        };

        // when/then
        await Should.ThrowAsync<ArgumentException>(async () => await _tool.Run(invalidParams));
    }

    #region Helper Methods

    private static DownloadItem CreateDownloadItem(int downloadId, string title, DownloadStatus status)
    {
        return new DownloadItem
        {
            Id = downloadId,
            Status = status,
            Title = title,
            Link = $"https://example.com/download/{downloadId}",
            SavePath = $"/downloads/{title.ToLower().Replace(" ", "-")}"
        };
    }

    private void SetupMockClientForInProgressThenCompleted(
        int downloadId, DownloadItem inProgressItem, DownloadItem completedItem)
    {
        _mockClient.Setup(c => c.GetDownloadItem(downloadId, CancellationToken.None))
            .ReturnsAsync(inProgressItem);
    }

    private void SetupMockClientForCompletedDownload(int downloadId, DownloadItem completedItem)
    {
        _mockClient.Setup(c => c.GetDownloadItem(downloadId, CancellationToken.None))
            .ReturnsAsync(completedItem);
    }

    private void SetupMockClientForCancellation(
        int downloadId, DownloadItem inProgressItem, CancellationTokenSource cancellationTokenSource)
    {
        _mockClient.Setup(c => c.GetDownloadItem(downloadId, CancellationToken.None))
            .ReturnsAsync(inProgressItem);
    }

    private static void AssertSuccessResult(JsonNode result, int downloadId)
    {
        result.ShouldBeOfType<JsonObject>();
        result["status"]?.ToString().ShouldBe("success");
        result["downloadId"]?.GetValue<int>().ShouldBe(downloadId);
        result["message"]?.ToString().ShouldNotBeNullOrEmpty();
    }

    private void VerifyGetDownloadItemCalled(int downloadId, Times times)
    {
        _mockClient.Verify(c => c.GetDownloadItem(downloadId, CancellationToken.None), times);
    }

    #endregion
}
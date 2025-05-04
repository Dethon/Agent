using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Exceptions;
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

        _mockClient
            .SetupSequence(c => c.GetDownloadItem(downloadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(downloadItem)
            .ReturnsAsync(completedItem);

        // when
        var result = await _tool.Run(parameters);

        // then
        AssertSuccessResult(result, downloadId);
        _mockClient.Verify(c => c.GetDownloadItem(downloadId, It.IsAny<CancellationToken>()), Times.Exactly(2));
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

        _mockClient
            .Setup(c => c.GetDownloadItem(downloadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(downloadItem);

        // ReSharper disable MethodSupportsCancellation
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            await cancellationTokenSource.CancelAsync();
        });

        // when/then
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await _tool.Run(parameters, cancellationTokenSource.Token);
        });
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

        _mockClient
            .Setup(c => c.GetDownloadItem(downloadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(completedItem);

        // when
        var result = await _tool.Run(parameters);

        // then
        AssertSuccessResult(result, downloadId);
        _mockClient.Verify(c => c.GetDownloadItem(downloadId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_ShouldThrowMissingDownloadException_WhenDownloadNotFound()
    {
        // given
        const int downloadId = 999;
        var parameters = new JsonObject
        {
            ["DownloadId"] = downloadId
        };

        _mockClient
            .Setup(c => c.GetDownloadItem(downloadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DownloadItem?)null);

        // when/then
        var exception = await Should.ThrowAsync<MissingDownloadException>(async () =>
            await _tool.Run(parameters));

        exception.Message.ShouldContain("download is missing");
        _mockClient.Verify(c => c.GetDownloadItem(downloadId, It.IsAny<CancellationToken>()), Times.Exactly(3));
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

    private static void AssertSuccessResult(JsonNode result, int downloadId)
    {
        result.ShouldBeOfType<JsonObject>();
        result["status"]?.ToString().ShouldBe("success");
        result["downloadId"]?.GetValue<int>().ShouldBe(downloadId);
        result["message"]?.ToString().ShouldNotBeNullOrEmpty();
    }

    #endregion
}
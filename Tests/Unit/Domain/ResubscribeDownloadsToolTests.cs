using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Downloads;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain;

public class ResubscribeDownloadsToolTests
{
    private readonly Mock<IDownloadClient> _downloadClientMock = new();
    private readonly Mock<ITrackedDownloadsManager> _trackedDownloadsManagerMock = new();

    private TestableResubscribeDownloadsTool CreateTool()
    {
        return new TestableResubscribeDownloadsTool(
            _downloadClientMock.Object,
            _trackedDownloadsManagerMock.Object);
    }

    [Fact]
    public async Task Run_WithEmptyDownloadIds_ReturnsErrorResponse()
    {
        // Arrange
        var tool = CreateTool();

        // Act
        var result = await tool.TestRun("session1", [], CancellationToken.None);

        // Assert
        result.HasNewSubscriptions.ShouldBeFalse();
        result.Response["status"]!.ToString().ShouldBe("error");
        result.Response["message"]!.ToString().ShouldBe("No download IDs provided");
    }

    [Fact]
    public async Task Run_WithAlreadyTrackedDownload_ReturnsAlreadyTrackedStatus()
    {
        // Arrange
        var tool = CreateTool();
        _trackedDownloadsManagerMock
            .Setup(m => m.Get("session1"))
            .Returns([123]);

        // Act
        var result = await tool.TestRun("session1", [123], CancellationToken.None);

        // Assert
        result.HasNewSubscriptions.ShouldBeFalse();
        var results = result.Response["results"]!.AsArray();
        results.Count.ShouldBe(1);
        results[0]!["status"]!.ToString().ShouldBe("AlreadyTracked");
    }

    [Fact]
    public async Task Run_WithNotFoundDownload_ReturnsNotFoundStatus()
    {
        // Arrange
        var tool = CreateTool();
        _trackedDownloadsManagerMock
            .Setup(m => m.Get("session1"))
            .Returns([]);
        _downloadClientMock
            .Setup(m => m.GetDownloadItem(123, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DownloadItem?)null);

        // Act
        var result = await tool.TestRun("session1", [123], CancellationToken.None);

        // Assert
        result.HasNewSubscriptions.ShouldBeFalse();
        var results = result.Response["results"]!.AsArray();
        results[0]!["status"]!.ToString().ShouldBe("NotFound");
        results[0]!["message"]!.ToString().ShouldContain("Check the downloads folder");
    }

    [Fact]
    public async Task Run_WithCompletedDownload_ReturnsAlreadyCompletedStatus()
    {
        // Arrange
        var tool = CreateTool();
        _trackedDownloadsManagerMock
            .Setup(m => m.Get("session1"))
            .Returns([]);
        _downloadClientMock
            .Setup(m => m.GetDownloadItem(123, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateDownloadItem(123, DownloadState.Completed));

        // Act
        var result = await tool.TestRun("session1", [123], CancellationToken.None);

        // Assert
        result.HasNewSubscriptions.ShouldBeFalse();
        var results = result.Response["results"]!.AsArray();
        results[0]!["status"]!.ToString().ShouldBe("AlreadyCompleted");
        results[0]!["message"]!.ToString().ShouldContain("Check the downloads folder");
    }

    [Fact]
    public async Task Run_WithInProgressDownload_ResubscribesAndReturnsSuccess()
    {
        // Arrange
        var tool = CreateTool();
        _trackedDownloadsManagerMock
            .Setup(m => m.Get("session1"))
            .Returns([]);
        _downloadClientMock
            .Setup(m => m.GetDownloadItem(123, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateDownloadItem(123, DownloadState.InProgress));

        // Act
        var result = await tool.TestRun("session1", [123], CancellationToken.None);

        // Assert
        result.HasNewSubscriptions.ShouldBeTrue();
        var results = result.Response["results"]!.AsArray();
        results[0]!["status"]!.ToString().ShouldBe("Resubscribed");
        _trackedDownloadsManagerMock.Verify(m => m.Add("session1", 123), Times.Once);
    }

    [Fact]
    public async Task Run_WithPausedDownload_ResubscribesAndReturnsSuccess()
    {
        // Arrange
        var tool = CreateTool();
        _trackedDownloadsManagerMock
            .Setup(m => m.Get("session1"))
            .Returns([]);
        _downloadClientMock
            .Setup(m => m.GetDownloadItem(123, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateDownloadItem(123, DownloadState.Paused));

        // Act
        var result = await tool.TestRun("session1", [123], CancellationToken.None);

        // Assert
        result.HasNewSubscriptions.ShouldBeTrue();
        var results = result.Response["results"]!.AsArray();
        results[0]!["status"]!.ToString().ShouldBe("Resubscribed");
    }

    [Fact]
    public async Task Run_WithMixedDownloads_ReturnsCorrectStatuses()
    {
        // Arrange
        var tool = CreateTool();
        _trackedDownloadsManagerMock
            .Setup(m => m.Get("session1"))
            .Returns([100]); // 100 is already tracked

        _downloadClientMock
            .Setup(m => m.GetDownloadItem(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DownloadItem?)null); // Not found

        _downloadClientMock
            .Setup(m => m.GetDownloadItem(300, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateDownloadItem(300, DownloadState.Completed)); // Completed

        _downloadClientMock
            .Setup(m => m.GetDownloadItem(400, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateDownloadItem(400, DownloadState.InProgress)); // In progress

        // Act
        var result = await tool.TestRun("session1", [100, 200, 300, 400], CancellationToken.None);

        // Assert
        result.HasNewSubscriptions.ShouldBeTrue();
        var summary = result.Response["summary"]!;
        summary["resubscribed"]!.GetValue<int>().ShouldBe(1);
        summary["needsAttention"]!.GetValue<int>().ShouldBe(2);
        summary["alreadyTracked"]!.GetValue<int>().ShouldBe(1);

        var results = result.Response["results"]!.AsArray();
        results.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Run_WithAllNeedingAttention_ReturnsAttentionRequiredStatus()
    {
        // Arrange
        var tool = CreateTool();
        _trackedDownloadsManagerMock
            .Setup(m => m.Get("session1"))
            .Returns([]);
        _downloadClientMock
            .Setup(m => m.GetDownloadItem(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DownloadItem?)null);

        // Act
        var result = await tool.TestRun("session1", [123, 456], CancellationToken.None);

        // Assert
        result.HasNewSubscriptions.ShouldBeFalse();
        result.Response["status"]!.ToString().ShouldBe("attention_required");
    }

    private static DownloadItem CreateDownloadItem(int id, DownloadState state)
    {
        return new DownloadItem
        {
            Id = id,
            Title = $"Download {id}",
            Link = $"magnet:?xt=urn:btih:{id}",
            SavePath = $"/downloads/{id}",
            State = state,
            Progress = state == DownloadState.Completed ? 100 : 50,
            DownSpeed = 1000,
            UpSpeed = 100,
            Eta = 3600
        };
    }

    private class TestableResubscribeDownloadsTool(
        IDownloadClient downloadClient,
        ITrackedDownloadsManager trackedDownloadsManager)
        : ResubscribeDownloadsTool(downloadClient, trackedDownloadsManager)
    {
        public Task<ResubscribeDownloadsResult> TestRun(string sessionId, int[] downloadIds, CancellationToken ct)
        {
            return Run(sessionId, downloadIds, ct);
        }
    }
}
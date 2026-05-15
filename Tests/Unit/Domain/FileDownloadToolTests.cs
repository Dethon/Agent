using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Config;
using Domain.Tools.Downloads;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain;

public class FileDownloadToolTests
{
    private readonly Mock<IDownloadClient> _downloadClientMock = new();
    private readonly Mock<ISearchResultsManager> _searchResultsManagerMock = new();
    private readonly Mock<ITrackedDownloadsManager> _trackedDownloadsManagerMock = new();
    private readonly DownloadPathConfig _pathConfig = new("/downloads");

    private TestableFileDownloadTool CreateTool()
    {
        return new TestableFileDownloadTool(
            _downloadClientMock.Object,
            _searchResultsManagerMock.Object,
            _trackedDownloadsManagerMock.Object,
            _pathConfig);
    }

    [Fact]
    public async Task Run_SearchId_HappyPath_StartsDownloadAndTracks()
    {
        // Arrange
        const int searchResultId = 42;
        const string link = "magnet:?xt=urn:btih:abc";
        var searchResult = new SearchResult
        {
            Id = searchResultId,
            Title = "The Lost City of Z 1080p",
            Link = link
        };
        _downloadClientMock
            .Setup(m => m.GetDownloadItem(searchResultId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DownloadItem?)null);
        _searchResultsManagerMock
            .Setup(m => m.Get("session1", searchResultId))
            .Returns(searchResult);

        var tool = CreateTool();

        // Act
        var result = await tool.TestRun("session1", searchResultId, CancellationToken.None);

        // Assert
        result["status"]!.GetValue<string>().ShouldBe("success");
        _downloadClientMock.Verify(
            m => m.Download(link, "/downloads/42", searchResultId, It.IsAny<CancellationToken>()),
            Times.Once);
        _trackedDownloadsManagerMock.Verify(m => m.Add("session1", searchResultId), Times.Once);
    }

    [Fact]
    public async Task Run_SearchId_AlreadyExists_ReturnsAlreadyExistsEnvelope()
    {
        // Arrange
        const int searchResultId = 42;
        _downloadClientMock
            .Setup(m => m.GetDownloadItem(searchResultId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadItem
            {
                Id = searchResultId,
                Title = "x",
                Link = "x",
                SavePath = "x",
                State = DownloadState.InProgress,
                Progress = 0,
                DownSpeed = 0,
                UpSpeed = 0,
                Eta = 0
            });

        var tool = CreateTool();

        // Act
        var result = await tool.TestRun("session1", searchResultId, CancellationToken.None);

        // Assert
        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("already_exists");
        _downloadClientMock.Verify(
            m => m.Download(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_SearchId_NotInCache_ReturnsNotFoundEnvelope()
    {
        // Arrange
        const int searchResultId = 42;
        _downloadClientMock
            .Setup(m => m.GetDownloadItem(searchResultId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DownloadItem?)null);
        _searchResultsManagerMock
            .Setup(m => m.Get("session1", searchResultId))
            .Returns((SearchResult?)null);

        var tool = CreateTool();

        // Act
        var result = await tool.TestRun("session1", searchResultId, CancellationToken.None);

        // Assert
        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("not_found");
        _downloadClientMock.Verify(
            m => m.Download(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_Link_HappyPath_SeedsCacheAndStartsDownload()
    {
        // Arrange
        const string link = "magnet:?xt=urn:btih:web-found";
        const string title = "Web Found Title 1080p";
        var expectedId = link.GetHashCode();

        _downloadClientMock
            .Setup(m => m.GetDownloadItem(expectedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DownloadItem?)null);

        var tool = CreateTool();

        // Act
        var result = await tool.TestRun("session1", link, title, CancellationToken.None);

        // Assert
        result["status"]!.GetValue<string>().ShouldBe("success");

        _searchResultsManagerMock.Verify(
            m => m.Add(
                "session1",
                It.Is<SearchResult[]>(arr =>
                    arr.Length == 1 &&
                    arr[0].Id == expectedId &&
                    arr[0].Title == title &&
                    arr[0].Link == link)),
            Times.Once);

        _downloadClientMock.Verify(
            m => m.Download(link, $"/downloads/{expectedId}", expectedId, It.IsAny<CancellationToken>()),
            Times.Once);
        _trackedDownloadsManagerMock.Verify(m => m.Add("session1", expectedId), Times.Once);
    }

    [Fact]
    public async Task Run_Link_AlreadyExists_ReturnsAlreadyExistsEnvelopeWithoutSeedingOrAdding()
    {
        // Arrange
        const string link = "magnet:?xt=urn:btih:dup";
        const string title = "Duplicate";
        var id = link.GetHashCode();

        _downloadClientMock
            .Setup(m => m.GetDownloadItem(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadItem
            {
                Id = id,
                Title = "x",
                Link = link,
                SavePath = "x",
                State = DownloadState.InProgress,
                Progress = 0,
                DownSpeed = 0,
                UpSpeed = 0,
                Eta = 0
            });

        var tool = CreateTool();

        // Act
        var result = await tool.TestRun("session1", link, title, CancellationToken.None);

        // Assert
        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("already_exists");

        _searchResultsManagerMock.Verify(
            m => m.Add(It.IsAny<string>(), It.IsAny<SearchResult[]>()),
            Times.Never);
        _downloadClientMock.Verify(
            m => m.Download(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _trackedDownloadsManagerMock.Verify(
            m => m.Add(It.IsAny<string>(), It.IsAny<int>()),
            Times.Never);
    }

    private class TestableFileDownloadTool(
        IDownloadClient client,
        ISearchResultsManager searchResultsManager,
        ITrackedDownloadsManager trackedDownloadsManager,
        DownloadPathConfig pathConfig)
        : FileDownloadTool(client, searchResultsManager, trackedDownloadsManager, pathConfig)
    {
        public Task<JsonNode> TestRun(string sessionId, int searchResultId, CancellationToken ct)
            => Run(sessionId, searchResultId, ct);

        public Task<JsonNode> TestRun(string sessionId, string link, string title, CancellationToken ct)
            => Run(sessionId, link, title, ct);
    }
}
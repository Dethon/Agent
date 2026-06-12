using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Tools.Config;
using Domain.Tools.Downloads;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain;

public class FileDownloadToolTests
{
    private readonly Mock<IDownloadClient> _downloadClientMock = new();
    private readonly Mock<ISearchResultsManager> _searchResultsManagerMock = new();
    private readonly FakeDownloadRoutingStore _routingStore = new();
    private readonly DownloadPathConfig _pathConfig = new("/downloads");

    private TestableFileDownloadTool CreateTool(TimeProvider? timeProvider = null)
    {
        return new TestableFileDownloadTool(
            _downloadClientMock.Object,
            _searchResultsManagerMock.Object,
            _routingStore,
            _pathConfig,
            timeProvider);
    }

    [Fact]
    public async Task Run_SearchId_NoContext_StartsDownloadWithoutRouting()
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
        var result = await tool.TestRun("session1", searchResultId, null, CancellationToken.None);

        // Assert
        result["status"]!.GetValue<string>().ShouldBe("success");
        _downloadClientMock.Verify(
            m => m.Download(link, "/downloads/42", searchResultId, It.IsAny<CancellationToken>()),
            Times.Once);
        _routingStore.Items.ShouldBeEmpty();
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
        var result = await tool.TestRun("session1", searchResultId, null, CancellationToken.None);

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
        var result = await tool.TestRun("session1", searchResultId, null, CancellationToken.None);

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
        var result = await tool.TestRun("session1", link, title, null, CancellationToken.None);

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
        var result = await tool.TestRun("session1", link, title, null, CancellationToken.None);

        // Assert
        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("already_exists");

        _searchResultsManagerMock.Verify(
            m => m.Add(It.IsAny<string>(), It.IsAny<SearchResult[]>()),
            Times.Never);
        _downloadClientMock.Verify(
            m => m.Download(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_WithContext_StoresRoutingSnapshotWithTitle()
    {
        // Arrange
        const int searchResultId = 99;
        const string link = "magnet:?xt=urn:btih:ctx-test";
        const string title = "Context Title 4K";
        var context = new ConversationContext("agent1", "conv1", "user1", new ReplyTarget("signalr", "conv1"));
        var searchResult = new SearchResult { Id = searchResultId, Title = title, Link = link };
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 6, 12, 10, 30, 0, TimeSpan.Zero));

        _downloadClientMock
            .Setup(m => m.GetDownloadItem(searchResultId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DownloadItem?)null);
        _searchResultsManagerMock
            .Setup(m => m.Get("session1", searchResultId))
            .Returns(searchResult);

        var tool = CreateTool(timeProvider);

        // Act
        var result = await tool.TestRun("session1", searchResultId, context, CancellationToken.None);

        // Assert
        result["status"]!.GetValue<string>().ShouldBe("success");
        _routingStore.Items.Count.ShouldBe(1);
        var routing = _routingStore.Items[0];
        routing.DownloadId.ShouldBe(searchResultId);
        routing.Title.ShouldBe(title);
        routing.Context.ShouldBe(context);
        routing.SubmittedAt.ShouldBe(timeProvider.GetUtcNow());
    }

    [Fact]
    public async Task Run_WithoutContext_StoresNothing()
    {
        // Arrange
        const int searchResultId = 77;
        const string link = "magnet:?xt=urn:btih:no-ctx";
        var searchResult = new SearchResult { Id = searchResultId, Title = "No Context Title", Link = link };

        _downloadClientMock
            .Setup(m => m.GetDownloadItem(searchResultId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DownloadItem?)null);
        _searchResultsManagerMock
            .Setup(m => m.Get("session1", searchResultId))
            .Returns(searchResult);

        var tool = CreateTool();

        // Act
        var result = await tool.TestRun("session1", searchResultId, null, CancellationToken.None);

        // Assert
        result["status"]!.GetValue<string>().ShouldBe("success");
        _routingStore.Items.ShouldBeEmpty();
        result["message"]!.GetValue<string>().ShouldContain("alert");
        result["message"]!.GetValue<string>().ShouldContain("/downloads");
    }

    private class FakeDownloadRoutingStore : IDownloadRoutingStore
    {
        public List<DownloadRouting> Items { get; } = [];

        public Task SetAsync(DownloadRouting routing, CancellationToken ct = default)
        {
            Items.Add(routing);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DownloadRouting>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DownloadRouting>>(Items);

        public Task RemoveAsync(int downloadId, CancellationToken ct = default)
        {
            Items.RemoveAll(r => r.DownloadId == downloadId);
            return Task.CompletedTask;
        }
    }

    private class TestableFileDownloadTool(
        IDownloadClient client,
        ISearchResultsManager searchResultsManager,
        IDownloadRoutingStore routingStore,
        DownloadPathConfig pathConfig,
        TimeProvider? timeProvider = null)
        : FileDownloadTool(client, searchResultsManager, routingStore, pathConfig, timeProvider)
    {
        public Task<JsonNode> TestRun(string sessionId, int searchResultId, ConversationContext? context, CancellationToken ct)
            => Run(sessionId, searchResultId, context, ct);

        public Task<JsonNode> TestRun(string sessionId, string link, string title, ConversationContext? context, CancellationToken ct)
            => Run(sessionId, link, title, context, ct);
    }
}
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Attachments;
using Moq;
using Shouldly;

namespace Tests.Unit.Attachments;

public class DownloadMonitorTests
{
    private readonly Mock<IDownloadClient> _mockClient = new();

    [Fact]
    public void Add_ShouldAddDownloadToCollection()
    {
        // given
        var monitor = new DownloadMonitor(_mockClient.Object);
        var searchResult = CreateSearchResult(1);
        const string savePath = @"C:\test\path";

        // when
        monitor.Add(searchResult, savePath);

        // then
        monitor.Downloads.Count.ShouldBe(1);
        monitor.Downloads[1].Id.ShouldBe(1);
        monitor.Downloads[1].Title.ShouldBe("Test Item");
        monitor.Downloads[1].SavePath.ShouldBe(savePath);
        monitor.Downloads[1].Status.ShouldBe(DownloadStatus.Added);
    }

    [Fact]
    public void Add_ShouldThrowException_WhenAddingDuplicateId()
    {
        // given
        var monitor = new DownloadMonitor(_mockClient.Object);
        var searchResult = CreateSearchResult(1);
        const string savePath = @"C:\test\path";
        monitor.Add(searchResult, savePath);

        // when/then
        Should.Throw<Exception>(() => monitor.Add(searchResult, savePath))
            .Message.ShouldBe("Download already exists");
    }

    [Fact]
    public async Task AreDownloadsPending_ShouldReturnTrue_WhenDownloadsExist()
    {
        // given
        var monitor = new DownloadMonitor(_mockClient.Object);
        var searchResult = CreateSearchResult(1);
        const string savePath = @"C:\test\path";
        monitor.Add(searchResult, savePath);

        var expectedItems = new List<DownloadItem>
        {
            CreateDownloadItem(1, "Test Item", savePath, DownloadStatus.InProgress)
        };

        SetupMockClientToReturn(expectedItems);

        // when
        var result = await monitor.AreDownloadsPending();

        // then
        result.ShouldBeTrue();
        _mockClient.Verify(c => c.RefreshDownloadItems(
            It.IsAny<IEnumerable<DownloadItem>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AreDownloadsPending_ShouldReturnFalse_WhenNoDownloadsExist()
    {
        // given
        var monitor = new DownloadMonitor(_mockClient.Object);
        SetupMockClientToReturn(new List<DownloadItem>());

        // when
        var result = await monitor.AreDownloadsPending();

        // then
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task PopCompletedDownload_ShouldReturnTrueAndRemoveCompletedDownload()
    {
        // given
        var monitor = new DownloadMonitor(_mockClient.Object);

        // Add three downloads
        monitor.Add(CreateSearchResult(1, "Item 1"), "path1");
        monitor.Add(CreateSearchResult(2, "Item 2"), "path2");
        monitor.Add(CreateSearchResult(3, "Item 3"), "path3");

        // Set up mock to return one in progress and two completed
        var refreshedItems = new List<DownloadItem>
        {
            CreateDownloadItem(1, "Item 1", "path1", DownloadStatus.InProgress),
            CreateDownloadItem(2, "Item 2", "path2", DownloadStatus.Completed),
            CreateDownloadItem(3, "Item 3", "path3", DownloadStatus.Completed)
        };

        SetupMockClientToReturn(refreshedItems);

        // when
        var completedIds = await monitor.PopCompletedDownload(2);

        // then
        completedIds.ShouldBe(true);
        monitor.Downloads.Count.ShouldBe(2);
        monitor.Downloads.ShouldContainKey(1);
        monitor.Downloads.ShouldNotContainKey(2);
        monitor.Downloads.ShouldContainKey(3);
    }

    [Fact]
    public async Task PopCompletedDownload_ShouldReturnFalseForIncompleteDownload()
    {
        // given
        var monitor = new DownloadMonitor(_mockClient.Object);

        // Add three downloads
        monitor.Add(CreateSearchResult(1, "Item 1"), "path1");
        monitor.Add(CreateSearchResult(2, "Item 2"), "path2");
        monitor.Add(CreateSearchResult(3, "Item 3"), "path3");

        // Set up mock to return one in progress and two completed
        var refreshedItems = new List<DownloadItem>
        {
            CreateDownloadItem(1, "Item 1", "path1", DownloadStatus.InProgress),
            CreateDownloadItem(2, "Item 2", "path2", DownloadStatus.Completed),
            CreateDownloadItem(3, "Item 3", "path3", DownloadStatus.Completed)
        };

        SetupMockClientToReturn(refreshedItems);

        // when
        var completedIds = await monitor.PopCompletedDownload(1);

        // then
        completedIds.ShouldBe(false);
        monitor.Downloads.Count.ShouldBe(3);
        monitor.Downloads.ShouldContainKey(1);
        monitor.Downloads.ShouldContainKey(2);
        monitor.Downloads.ShouldContainKey(3);
    }

    [Fact]
    public async Task PopCompletedDownloads_ShouldReturnAndRemoveCompletedDownloads()
    {
        // given
        var monitor = new DownloadMonitor(_mockClient.Object);

        // Add three downloads
        monitor.Add(CreateSearchResult(1, "Item 1"), "path1");
        monitor.Add(CreateSearchResult(2, "Item 2"), "path2");
        monitor.Add(CreateSearchResult(3, "Item 3"), "path3");

        // Set up mock to return one in progress and two completed
        var refreshedItems = new List<DownloadItem>
        {
            CreateDownloadItem(1, "Item 1", "path1", DownloadStatus.InProgress),
            CreateDownloadItem(2, "Item 2", "path2", DownloadStatus.Completed),
            CreateDownloadItem(3, "Item 3", "path3", DownloadStatus.Completed)
        };

        SetupMockClientToReturn(refreshedItems);

        // when
        var completedIds = await monitor.PopCompletedDownloads();

        // then
        completedIds.ShouldBe([2, 3]);
        monitor.Downloads.Count.ShouldBe(1);
        monitor.Downloads.ShouldContainKey(1);
        monitor.Downloads.ShouldNotContainKey(2);
        monitor.Downloads.ShouldNotContainKey(3);
    }

    [Fact]
    public async Task PopCompletedDownloads_ShouldReturnEmptyArray_WhenNoCompletedDownloads()
    {
        // given
        var monitor = new DownloadMonitor(_mockClient.Object);
        monitor.Add(CreateSearchResult(1, "Item 1"), "path1");

        var refreshedItems = new List<DownloadItem>
        {
            CreateDownloadItem(1, "Item 1", "path1", DownloadStatus.InProgress)
        };

        SetupMockClientToReturn(refreshedItems);

        // when
        var completedIds = await monitor.PopCompletedDownloads();

        // then
        completedIds.ShouldBeEmpty();
        monitor.Downloads.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Refresh_ShouldUpdateDownloadsFromClient()
    {
        // given
        var monitor = new DownloadMonitor(_mockClient.Object);
        const string savePath = @"C:\test\path";
        monitor.Add(CreateSearchResult(1), savePath);

        var updatedItem = CreateDownloadItem(1, "Test Item", savePath, DownloadStatus.InProgress);

        SetupMockClientToReturn(new List<DownloadItem>
        {
            updatedItem
        });

        // when
        await monitor.AreDownloadsPending(); // This internally calls Refresh

        // then
        monitor.Downloads[1].Status.ShouldBe(DownloadStatus.InProgress);
    }

    #region Helper Methods

    private static SearchResult CreateSearchResult(int id, string title = "Test Item")
    {
        return new SearchResult
        {
            Id = id,
            Title = title,
            Link = $"https://test.com/{id}",
            Category = "Test",
            Size = 1000,
            Seeders = 10,
            Peers = 5
        };
    }

    private void SetupMockClientToReturn(IEnumerable<DownloadItem> items)
    {
        _mockClient.Setup(c => c.RefreshDownloadItems(
                It.IsAny<IEnumerable<DownloadItem>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);
    }

    private static DownloadItem CreateDownloadItem(int id, string title, string savePath, DownloadStatus status)
    {
        return new DownloadItem
        {
            Id = id,
            Title = title,
            Link = $"https://test.com/{id}",
            SavePath = savePath,
            Status = status
        };
    }

    #endregion
}
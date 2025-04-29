using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools;
using Domain.Tools.Attachments;
using Moq;
using Shouldly;

namespace Tests.Unit.Tools;

public class FileDownloadToolTests
{
    private readonly Mock<IDownloadClient> _mockClient;
    private readonly SearchHistory _searchHistory;
    private readonly DownloadMonitor _downloadMonitor;
    private readonly string _baseDownloadLocation;
    private readonly FileDownloadTool _sut;

    public FileDownloadToolTests()
    {
        _mockClient = new Mock<IDownloadClient>();
        _searchHistory = new SearchHistory();
        _downloadMonitor = new DownloadMonitor(_mockClient.Object);
        _baseDownloadLocation = "/test/downloads";

        _sut = new FileDownloadTool(
            _mockClient.Object,
            _searchHistory,
            _downloadMonitor,
            _baseDownloadLocation);
    }

    [Fact]
    public async Task Run_WithValidParameters_ShouldDownloadFileAndReturnSuccess()
    {
        // given
        const int searchResultId = 42;
        var searchResult = SetupSearchResult(searchResultId);
        var parameters = new JsonObject
        {
            ["SearchResultId"] = searchResultId
        };
        var expectedSavePath = GetExpectedSavePath(searchResultId);

        SetupDownloadClientSuccess(searchResult.Link, expectedSavePath, searchResultId.ToString());

        // when
        var result = await _sut.Run(parameters);

        // then
        VerifyDownloadWasCalled(searchResult.Link, expectedSavePath, searchResultId.ToString());
        VerifySuccessResult(result, searchResultId);
        _downloadMonitor.Downloads.ShouldContainKey(searchResultId);
    }

    [Fact]
    public void GetToolDefinition_ShouldReturnCorrectDefinition()
    {
        // when
        var definition = _sut.GetToolDefinition();

        // then
        definition.ShouldNotBeNull();
        definition.Name.ShouldBe("FileDownload");
        definition.Description.ShouldContain("Download a file from the internet");
        definition.Description.ShouldContain("SearchResultId parameter");
    }

    [Fact]
    public async Task Run_WithInvalidSearchResultId_ShouldThrowException()
    {
        // given
        const int nonExistentId = 999;
        var parameters = new JsonObject
        {
            ["SearchResultId"] = nonExistentId
        };

        // when/then
        await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await _sut.Run(parameters));
    }

    [Fact]
    public async Task Run_WithDownloadFailure_ShouldPropagateException()
    {
        // given
        const int searchResultId = 42;
        var parameters = new JsonObject
        {
            ["SearchResultId"] = searchResultId
        };

        SetupDownloadClientFailure("Download failed");

        // when/then
        await Should.ThrowAsync<Exception>(async () =>
            await _sut.Run(parameters));
    }

    #region Helper Methods

    private SearchResult SetupSearchResult(
        int id, string title = "Test File", string link = "https://example.com/testfile.zip", int size = 10000000)
    {
        var searchResult = new SearchResult
        {
            Id = id,
            Title = title,
            Link = link,
            Size = size
        };
        _searchHistory.History[id] = searchResult;
        return searchResult;
    }


    private string GetExpectedSavePath(int searchResultId)
    {
        return $"{_baseDownloadLocation}/{searchResultId}";
    }

    private void SetupDownloadClientSuccess(string link, string savePath, string id)
    {
        _mockClient
            .Setup(c => c.Download(
                link,
                savePath,
                id,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private void SetupDownloadClientFailure(string errorMessage)
    {
        _mockClient
            .Setup(c => c.Download(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception(errorMessage));
    }

    private void VerifyDownloadWasCalled(string link, string savePath, string id)
    {
        _mockClient.Verify(c => c.Download(
                link,
                savePath,
                id,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static void VerifySuccessResult(JsonNode result, int searchResultId)
    {
        result.ShouldBeOfType<JsonObject>();
        result["status"]?.GetValue<string>().ShouldBe("success");
        result["message"]?.GetValue<string>().ShouldBe("Torrent added to qBittorrent successfully");
        result["downloadId"]?.GetValue<int>().ShouldBe(searchResultId);
    }

    #endregion
}
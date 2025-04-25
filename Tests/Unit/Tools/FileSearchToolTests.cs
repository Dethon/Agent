using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools;
using Domain.Tools.Attachments;
using Moq;
using Shouldly;

namespace Tests.Unit.Tools;

public class FileSearchToolTests
{
    private readonly Mock<ISearchClient> _mockSearchClient = new();
    private readonly SearchHistory _searchHistory = new();

    [Fact]
    public async Task Run_ShouldReturnProperJsonNodeWithSearchResults()
    {
        // given
        var searchResults = CreateSampleSearchResults();
        SetupSearchResults(searchResults);

        var fileSearchTool = new FileSearchTool(_mockSearchClient.Object, _searchHistory);
        var parameters = new JsonObject
        {
            ["SearchString"] = "test search"
        };

        // when
        var result = await fileSearchTool.Run(parameters);

        // then
        result.ShouldNotBeNull();
        result["status"]?.GetValue<string>().ShouldBe("success");
        result["message"]?.GetValue<string>().ShouldBe("File search completed successfully");
        result["totalResults"]?.GetValue<int>().ShouldBe(2);

        _mockSearchClient.Verify(x => x.Search("test search", It.IsAny<CancellationToken>()), Times.Once);
        _searchHistory.History.Count.ShouldBe(2);
        _searchHistory.History.ContainsKey(1).ShouldBeTrue();
        _searchHistory.History.ContainsKey(2).ShouldBeTrue();
    }

    [Fact]
    public async Task Run_WithEmptyResults_ShouldReturnZeroTotalResults()
    {
        // given
        SetupSearchResults([]);
        var fileSearchTool = new FileSearchTool(_mockSearchClient.Object, _searchHistory);
        var parameters = new JsonObject
        {
            ["SearchString"] = "test search"
        };

        // when
        var result = await fileSearchTool.Run(parameters);

        // then
        result.ShouldNotBeNull();
        result["status"]?.GetValue<string>().ShouldBe("success");
        result["totalResults"]?.GetValue<int>().ShouldBe(0);
        _searchHistory.History.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Run_WithInvalidParameters_ShouldThrowException()
    {
        // given
        var fileSearchTool = new FileSearchTool(_mockSearchClient.Object, _searchHistory);
        var parameters = new JsonObject();

        // when/then
        await Should.ThrowAsync<ArgumentException>(async () =>
            await fileSearchTool.Run(parameters));
    }

    [Fact]
    public void GetToolDefinition_ShouldReturnCorrectDefinition()
    {
        // given
        var fileSearchTool = new FileSearchTool(_mockSearchClient.Object, _searchHistory);

        // when
        var definition = fileSearchTool.GetToolDefinition();

        // then
        definition.ShouldNotBeNull();
        definition.Name.ShouldBe("FileSearch");
        definition.Description.ShouldContain("Search for a file in the internet");
    }

    #region Helper Methods

    private void SetupSearchResults(SearchResult[] results)
    {
        _mockSearchClient
            .Setup(x => x.Search(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(results);
    }

    private static SearchResult[] CreateSampleSearchResults()
    {
        return
        [
            new SearchResult
            {
                Title = "Test File 1",
                Id = 1,
                Link = "https://example.com/file1",
                Category = "Test Category",
                Size = 1000,
                Seeders = 10,
                Peers = 5
            },
            new SearchResult
            {
                Title = "Test File 2",
                Id = 2,
                Link = "https://example.com/file2",
                Category = null,
                Size = 2000,
                Seeders = 20,
                Peers = 15
            }
        ];
    }

    #endregion
}
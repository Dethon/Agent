using Domain.DTOs;
using Domain.Tools.Attachments;
using Shouldly;

namespace Tests.Unit.Attachments;

public class SearchHistoryTests
{
    [Fact]
    public void Add_EmptyCollection_HistoryRemainsEmpty()
    {
        // given
        var searchHistory = new SearchHistory();

        // when
        searchHistory.Add([]);

        // then
        searchHistory.History.ShouldBeEmpty();
    }

    [Fact]
    public void Add_SingleResult_ResultIsAddedToHistory()
    {
        // given
        var searchHistory = new SearchHistory();
        var result = new SearchResult
        {
            Id = 1,
            Title = "Test Result",
            Link = "https://example.com"
        };

        // when
        searchHistory.Add([result]);

        // then
        searchHistory.History.Count.ShouldBe(1);
        searchHistory.History[1].ShouldBe(result);
    }

    [Fact]
    public void Add_MultipleResults_AllResultsAreAddedToHistory()
    {
        // given
        var searchHistory = new SearchHistory();
        var results = new[]
        {
            new SearchResult
            {
                Id = 1,
                Title = "Result 1",
                Link = "https://example.com/1"
            },
            new SearchResult
            {
                Id = 2,
                Title = "Result 2",
                Link = "https://example.com/2"
            },
            new SearchResult
            {
                Id = 3,
                Title = "Result 3",
                Link = "https://example.com/3"
            }
        };

        // when
        searchHistory.Add(results);

        // then
        searchHistory.History.Count.ShouldBe(3);
        searchHistory.History[1].ShouldBe(results[0]);
        searchHistory.History[2].ShouldBe(results[1]);
        searchHistory.History[3].ShouldBe(results[2]);
    }

    [Fact]
    public void Add_DuplicateIds_OnlyLatestResultIsKept()
    {
        // given
        var searchHistory = new SearchHistory();
        var originalResult = new SearchResult
        {
            Id = 1,
            Title = "Original",
            Link = "https://example.com/original"
        };
        var updatedResult = new SearchResult
        {
            Id = 1,
            Title = "Updated",
            Link = "https://example.com/updated"
        };

        // when
        searchHistory.Add([originalResult]);
        searchHistory.Add([updatedResult]);

        // then
        searchHistory.History.Count.ShouldBe(1);
        searchHistory.History[1].ShouldBe(updatedResult);
        searchHistory.History[1].Title.ShouldBe("Updated");
    }

    [Fact]
    public void Add_MultipleBatches_AllUniqueResultsAreKept()
    {
        // given
        var searchHistory = new SearchHistory();
        var firstBatch = new[]
        {
            new SearchResult
            {
                Id = 1,
                Title = "Result 1",
                Link = "https://example.com/1"
            },
            new SearchResult
            {
                Id = 2,
                Title = "Result 2",
                Link = "https://example.com/2"
            }
        };
        var secondBatch = new[]
        {
            new SearchResult
            {
                Id = 3,
                Title = "Result 3",
                Link = "https://example.com/3"
            },
            new SearchResult
            {
                Id = 4,
                Title = "Result 4",
                Link = "https://example.com/4"
            }
        };

        // when
        searchHistory.Add(firstBatch);
        searchHistory.Add(secondBatch);

        // then
        searchHistory.History.Count.ShouldBe(4);
        searchHistory.History.ShouldContainKey(1);
        searchHistory.History.ShouldContainKey(2);
        searchHistory.History.ShouldContainKey(3);
        searchHistory.History.ShouldContainKey(4);
    }

    [Fact]
    public void Add_MixedNewAndDuplicateIds_PreservesUniqueAndUpdatesExisting()
    {
        // given
        var searchHistory = new SearchHistory();
        var firstBatch = new[]
        {
            new SearchResult
            {
                Id = 1,
                Title = "Result 1",
                Link = "https://example.com/1"
            },
            new SearchResult
            {
                Id = 2,
                Title = "Result 2",
                Link = "https://example.com/2"
            }
        };
        var secondBatch = new[]
        {
            new SearchResult
            {
                Id = 1,
                Title = "Updated Result 1",
                Link = "https://example.com/1-updated"
            },
            new SearchResult
            {
                Id = 3,
                Title = "Result 3",
                Link = "https://example.com/3"
            }
        };

        // when
        searchHistory.Add(firstBatch);
        searchHistory.Add(secondBatch);

        // then
        searchHistory.History.Count.ShouldBe(3);
        searchHistory.History[1].Title.ShouldBe("Updated Result 1");
        searchHistory.History[2].Title.ShouldBe("Result 2");
        searchHistory.History[3].Title.ShouldBe("Result 3");
    }
}
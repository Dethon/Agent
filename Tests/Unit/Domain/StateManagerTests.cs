using Domain.DTOs;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Caching.Memory;
using Shouldly;

namespace Tests.Unit.Domain;

public class StateManagerTests
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    [Fact]
    public void SearchResultsManager_AddAndGet_Works()
    {
        // Arrange
        var manager = new SearchResultsManager(_cache);
        const string sessionId = "session-1";
        var results = new[] { CreateSearchResult(1, "Result 1"), CreateSearchResult(2, "Result 2") };

        // Act
        manager.Add(sessionId, results);
        var retrieved1 = manager.Get(sessionId, 1);
        var retrieved2 = manager.Get(sessionId, 2);

        // Assert
        retrieved1.ShouldNotBeNull();
        retrieved1.Title.ShouldBe("Result 1");
        retrieved2.ShouldNotBeNull();
        retrieved2.Title.ShouldBe("Result 2");
    }

    [Fact]
    public void SearchResultsManager_GetNonExistent_ReturnsNull()
    {
        // Arrange
        var manager = new SearchResultsManager(_cache);

        // Act
        var result = manager.Get("non-existent-session", 999);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public void SearchResultsManager_WithDifferentSessions_KeepsResultsSeparate()
    {
        // Arrange
        var manager = new SearchResultsManager(_cache);
        var session1Results = new[] { CreateSearchResult(1, "Session 1 Result") };
        var session2Results = new[] { CreateSearchResult(1, "Session 2 Result") };

        // Act
        manager.Add("session-1", session1Results);
        manager.Add("session-2", session2Results);

        // Assert
        manager.Get("session-1", 1)?.Title.ShouldBe("Session 1 Result");
        manager.Get("session-2", 1)?.Title.ShouldBe("Session 2 Result");
    }

    private static SearchResult CreateSearchResult(int id, string title)
    {
        return new SearchResult
        {
            Id = id,
            Title = title,
            Link = $"http://example.com/{id}"
        };
    }
}
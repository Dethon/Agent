using Domain.DTOs;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Caching.Memory;
using Shouldly;

namespace Tests.Integration.Domain;

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

    [Fact]
    public void TrackedDownloadsManager_Add_TracksDownload()
    {
        // Arrange
        var manager = new TrackedDownloadsManager(_cache);
        const string sessionId = "session-1";

        // Act
        manager.Add(sessionId, 100);
        manager.Add(sessionId, 200);

        // Assert
        var downloads = manager.Get(sessionId);
        downloads.ShouldNotBeNull();
        downloads.ShouldContain(100);
        downloads.ShouldContain(200);
    }

    [Fact]
    public void TrackedDownloadsManager_Remove_UntracksDownload()
    {
        // Arrange
        var manager = new TrackedDownloadsManager(_cache);
        const string sessionId = "session-1";
        manager.Add(sessionId, 100);
        manager.Add(sessionId, 200);

        // Act
        manager.Remove(sessionId, 100);

        // Assert
        var downloads = manager.Get(sessionId);
        downloads.ShouldNotBeNull();
        downloads.ShouldNotContain(100);
        downloads.ShouldContain(200);
    }

    [Fact]
    public void TrackedDownloadsManager_Get_ReturnsOrderedIds()
    {
        // Arrange
        var manager = new TrackedDownloadsManager(_cache);
        const string sessionId = "session-1";
        manager.Add(sessionId, 300);
        manager.Add(sessionId, 100);
        manager.Add(sessionId, 200);

        // Act
        var downloads = manager.Get(sessionId);

        // Assert
        downloads.ShouldNotBeNull();
        downloads.ShouldBe([100, 200, 300]);
    }

    [Fact]
    public void TrackedDownloadsManager_GetNonExistent_ReturnsNull()
    {
        // Arrange
        var manager = new TrackedDownloadsManager(_cache);

        // Act
        var result = manager.Get("non-existent-session");

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public void TrackedDownloadsManager_RemoveFromNonExistent_DoesNotThrow()
    {
        // Arrange
        var manager = new TrackedDownloadsManager(_cache);

        // Act & Assert
        Should.NotThrow(() => manager.Remove("non-existent-session", 123));
    }

    [Fact]
    public void TrackedDownloadsManager_AddDuplicate_DoesNotDuplicate()
    {
        // Arrange
        var manager = new TrackedDownloadsManager(_cache);
        const string sessionId = "session-1";

        // Act
        manager.Add(sessionId, 100);
        manager.Add(sessionId, 100);

        // Assert
        var downloads = manager.Get(sessionId);
        downloads.ShouldNotBeNull();
        downloads.Length.ShouldBe(1);
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
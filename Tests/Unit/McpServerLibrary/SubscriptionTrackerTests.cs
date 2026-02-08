using McpServerLibrary.ResourceSubscriptions;
using ModelContextProtocol.Server;
using Moq;
using Shouldly;

namespace Tests.Unit.McpServerLibrary;

public class SubscriptionTrackerTests
{
    private static McpServer CreateMockServer()
    {
        return new Mock<McpServer>().Object;
    }

    [Fact]
    public void RemoveSession_WithExistingSession_RemovesAllSubscriptions()
    {
        // Arrange
        var tracker = new SubscriptionTracker();
        var server = CreateMockServer();
        tracker.Add("session1", "download://1/", server);
        tracker.Add("session1", "download://2/", server);

        // Act
        tracker.RemoveSession("session1");

        // Assert
        var subscriptions = tracker.Get();
        subscriptions.ShouldNotContainKey("session1");
    }

    [Fact]
    public void RemoveSession_WithNonExistentSession_DoesNotThrow()
    {
        // Arrange
        var tracker = new SubscriptionTracker();

        // Act & Assert
        Should.NotThrow(() => tracker.RemoveSession("nonexistent"));
    }

    [Fact]
    public void RemoveSession_WithMultipleSessions_OnlyRemovesSpecifiedSession()
    {
        // Arrange
        var tracker = new SubscriptionTracker();
        var server = CreateMockServer();
        tracker.Add("session1", "download://1/", server);
        tracker.Add("session2", "download://2/", server);

        // Act
        tracker.RemoveSession("session1");

        // Assert
        var subscriptions = tracker.Get();
        subscriptions.ShouldNotContainKey("session1");
        subscriptions.ShouldContainKey("session2");
        subscriptions["session2"].ShouldContainKey("download://2/");
    }

    [Fact]
    public void Add_WithNewSession_CreatesSessionEntry()
    {
        // Arrange
        var tracker = new SubscriptionTracker();
        var server = CreateMockServer();

        // Act
        tracker.Add("session1", "download://1/", server);

        // Assert
        var subscriptions = tracker.Get();
        subscriptions.ShouldContainKey("session1");
        subscriptions["session1"].ShouldContainKey("download://1/");
    }

    [Fact]
    public void Remove_LastUri_RemovesSessionEntry()
    {
        // Arrange
        var tracker = new SubscriptionTracker();
        var server = CreateMockServer();
        tracker.Add("session1", "download://1/", server);

        // Act
        tracker.Remove("session1", "download://1/");

        // Assert
        var subscriptions = tracker.Get();
        subscriptions.ShouldNotContainKey("session1");
    }

    [Fact]
    public void Remove_OneOfMultipleUris_KeepsSessionWithRemainingUris()
    {
        // Arrange
        var tracker = new SubscriptionTracker();
        var server = CreateMockServer();
        tracker.Add("session1", "download://1/", server);
        tracker.Add("session1", "download://2/", server);

        // Act
        tracker.Remove("session1", "download://1/");

        // Assert
        var subscriptions = tracker.Get();
        subscriptions.ShouldContainKey("session1");
        subscriptions["session1"].ShouldNotContainKey("download://1/");
        subscriptions["session1"].ShouldContainKey("download://2/");
    }
}
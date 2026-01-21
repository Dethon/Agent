using Domain.DTOs.WebChat;
using Moq;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Hub;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class HubEventDispatcherTests : IDisposable
{
    private readonly Mock<IDispatcher> _mockDispatcher;
    private readonly Dispatcher _realDispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly StreamingStore _streamingStore;
    private readonly Mock<IStreamResumeService> _mockStreamResumeService;
    private readonly HubEventDispatcher _sut;

    public HubEventDispatcherTests()
    {
        _mockDispatcher = new Mock<IDispatcher>();
        _realDispatcher = new Dispatcher();
        _topicsStore = new TopicsStore(_realDispatcher);
        _streamingStore = new StreamingStore(_realDispatcher);
        _mockStreamResumeService = new Mock<IStreamResumeService>();
        _sut = new HubEventDispatcher(
            _mockDispatcher.Object,
            _topicsStore,
            _streamingStore,
            _mockStreamResumeService.Object);
    }

    public void Dispose()
    {
        _topicsStore.Dispose();
        _streamingStore.Dispose();
    }

    private static TopicMetadata CreateTopicMetadata(string topicId = "topic-1") =>
        new(topicId, 123L, 456L, "agent-1", "Test Topic", DateTimeOffset.UtcNow, null);

    [Fact]
    public void HandleTopicChanged_Created_DispatchesAddTopic()
    {
        var metadata = CreateTopicMetadata();
        var notification = new TopicChangedNotification(TopicChangeType.Created, "topic-1", metadata);

        _sut.HandleTopicChanged(notification);

        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<AddTopic>(a => a.Topic.TopicId == "topic-1")),
            Times.Once);
    }

    [Fact]
    public void HandleTopicChanged_Updated_DispatchesUpdateTopic()
    {
        var metadata = CreateTopicMetadata();
        var notification = new TopicChangedNotification(TopicChangeType.Updated, "topic-1", metadata);

        _sut.HandleTopicChanged(notification);

        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<UpdateTopic>(a => a.Topic.TopicId == "topic-1")),
            Times.Once);
    }

    [Fact]
    public void HandleTopicChanged_Deleted_DispatchesRemoveTopic()
    {
        var notification = new TopicChangedNotification(TopicChangeType.Deleted, "topic-1");

        _sut.HandleTopicChanged(notification);

        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<RemoveTopic>(a => a.TopicId == "topic-1")),
            Times.Once);
    }

    [Fact]
    public void HandleStreamChanged_Started_TopicNotFound_DispatchesStreamStarted()
    {
        // Topic not in store, so StreamStarted should be dispatched
        var notification = new StreamChangedNotification(StreamChangeType.Started, "topic-1");

        _sut.HandleStreamChanged(notification);

        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<StreamStarted>(a => a.TopicId == "topic-1")),
            Times.Once);
    }

    [Fact]
    public void HandleStreamChanged_Started_TopicFound_CallsStreamResume()
    {
        // Add topic to store so TryResumeStreamAsync is called
        var topic = new StoredTopic
        {
            TopicId = "topic-1",
            ChatId = 123,
            ThreadId = 456,
            AgentId = "agent-1",
            Name = "Test Topic"
        };
        _realDispatcher.Dispatch(new AddTopic(topic));

        var notification = new StreamChangedNotification(StreamChangeType.Started, "topic-1");

        _sut.HandleStreamChanged(notification);

        _mockStreamResumeService.Verify(
            s => s.TryResumeStreamAsync(It.Is<StoredTopic>(t => t.TopicId == "topic-1")),
            Times.Once);
        // StreamStarted should NOT be dispatched when topic is found
        _mockDispatcher.Verify(
            d => d.Dispatch(It.IsAny<StreamStarted>()),
            Times.Never);
    }

    [Fact]
    public void HandleStreamChanged_Completed_DispatchesStreamCompleted()
    {
        var notification = new StreamChangedNotification(StreamChangeType.Completed, "topic-1");

        _sut.HandleStreamChanged(notification);

        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<StreamCompleted>(a => a.TopicId == "topic-1")),
            Times.Once);
    }

    [Fact]
    public void HandleStreamChanged_Cancelled_DispatchesStreamCancelled()
    {
        var notification = new StreamChangedNotification(StreamChangeType.Cancelled, "topic-1");

        _sut.HandleStreamChanged(notification);

        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<StreamCancelled>(a => a.TopicId == "topic-1")),
            Times.Once);
    }

    [Fact]
    public void HandleApprovalResolved_DispatchesApprovalResolved()
    {
        var notification = new ApprovalResolvedNotification("topic-1", "approval-123");

        _sut.HandleApprovalResolved(notification);

        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<ApprovalResolved>(a => a.ApprovalId == "approval-123")),
            Times.Once);
    }

    [Fact]
    public void HandleApprovalResolved_WithToolCalls_DispatchesStreamChunk()
    {
        var notification = new ApprovalResolvedNotification("topic-1", "approval-123", "tool output");

        _sut.HandleApprovalResolved(notification);

        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<StreamChunk>(a =>
                a.TopicId == "topic-1" &&
                a.ToolCalls == "tool output")),
            Times.Once);
    }

    [Fact]
    public void HandleApprovalResolved_WithoutToolCalls_DoesNotDispatchStreamChunk()
    {
        var notification = new ApprovalResolvedNotification("topic-1", "approval-123");

        _sut.HandleApprovalResolved(notification);

        _mockDispatcher.Verify(
            d => d.Dispatch(It.IsAny<StreamChunk>()),
            Times.Never);
    }

    [Fact]
    public void HandleToolCalls_DispatchesStreamChunk()
    {
        var notification = new ToolCallsNotification("topic-1", "tool output");

        _sut.HandleToolCalls(notification);

        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<StreamChunk>(a =>
                a.TopicId == "topic-1" &&
                a.ToolCalls == "tool output" &&
                a.Content == null &&
                a.Reasoning == null &&
                a.MessageId == null)),
            Times.Once);
    }
}
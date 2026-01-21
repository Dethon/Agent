using Moq;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Hub;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class ReconnectionEffectTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ConnectionStore _connectionStore;
    private readonly TopicsStore _topicsStore;
    private readonly Mock<IChatSessionService> _mockSessionService;
    private readonly Mock<IStreamResumeService> _mockStreamResumeService;
    private ReconnectionEffect? _sut;

    public ReconnectionEffectTests()
    {
        _dispatcher = new Dispatcher();
        _connectionStore = new ConnectionStore(_dispatcher);
        _topicsStore = new TopicsStore(_dispatcher);
        _mockSessionService = new Mock<IChatSessionService>();
        _mockStreamResumeService = new Mock<IStreamResumeService>();
    }

    private void CreateEffect()
    {
        _sut = new ReconnectionEffect(
            _connectionStore,
            _topicsStore,
            _mockSessionService.Object,
            _mockStreamResumeService.Object);
    }

    [Fact]
    public void WhenConnectionReconnected_StartsSessionForSelectedTopic()
    {
        var topic = new StoredTopic { TopicId = "topic-1", Name = "Test Topic" };
        _dispatcher.Dispatch(new TopicsLoaded([topic]));
        _dispatcher.Dispatch(new SelectTopic(topic.TopicId));

        _mockSessionService
            .Setup(s => s.StartSessionAsync(It.IsAny<StoredTopic>()))
            .ReturnsAsync(true);

        CreateEffect();

        // Simulate reconnection sequence: Connected -> Reconnecting -> Connected
        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnected());

        _mockSessionService.Verify(
            s => s.StartSessionAsync(It.Is<StoredTopic>(t => t.TopicId == "topic-1")),
            Times.Once);
    }

    [Fact]
    public void WhenConnectionReconnected_ResumesStreamsForAllTopics()
    {
        var topic1 = new StoredTopic { TopicId = "topic-1", Name = "Topic 1" };
        var topic2 = new StoredTopic { TopicId = "topic-2", Name = "Topic 2" };
        _dispatcher.Dispatch(new TopicsLoaded([topic1, topic2]));

        _mockStreamResumeService
            .Setup(s => s.TryResumeStreamAsync(It.IsAny<StoredTopic>()))
            .Returns(Task.CompletedTask);

        CreateEffect();

        // Simulate reconnection
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnected());

        _mockStreamResumeService.Verify(
            s => s.TryResumeStreamAsync(It.Is<StoredTopic>(t => t.TopicId == "topic-1")),
            Times.Once);
        _mockStreamResumeService.Verify(
            s => s.TryResumeStreamAsync(It.Is<StoredTopic>(t => t.TopicId == "topic-2")),
            Times.Once);
    }

    [Fact]
    public void WhenConnectionConnectedWithoutPriorReconnecting_DoesNotTriggerReconnection()
    {
        var topic = new StoredTopic { TopicId = "topic-1", Name = "Test Topic" };
        _dispatcher.Dispatch(new TopicsLoaded([topic]));
        _dispatcher.Dispatch(new SelectTopic(topic.TopicId));

        CreateEffect();

        // Fresh connection (not reconnection)
        _dispatcher.Dispatch(new ConnectionConnected());

        _mockSessionService.Verify(
            s => s.StartSessionAsync(It.IsAny<StoredTopic>()),
            Times.Never);
        _mockStreamResumeService.Verify(
            s => s.TryResumeStreamAsync(It.IsAny<StoredTopic>()),
            Times.Never);
    }

    [Fact]
    public void WhenConnectionReconnecting_DoesNotTriggerYet()
    {
        var topic = new StoredTopic { TopicId = "topic-1", Name = "Test Topic" };
        _dispatcher.Dispatch(new TopicsLoaded([topic]));
        _dispatcher.Dispatch(new SelectTopic(topic.TopicId));

        CreateEffect();

        // Only reconnecting, not yet reconnected
        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionReconnecting());

        _mockSessionService.Verify(
            s => s.StartSessionAsync(It.IsAny<StoredTopic>()),
            Times.Never);
        _mockStreamResumeService.Verify(
            s => s.TryResumeStreamAsync(It.IsAny<StoredTopic>()),
            Times.Never);
    }

    [Fact]
    public void Dispose_UnsubscribesFromStore()
    {
        CreateEffect();
        _sut!.Dispose();

        // After dispose, reconnection should not trigger callbacks
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnected());

        _mockSessionService.Verify(
            s => s.StartSessionAsync(It.IsAny<StoredTopic>()),
            Times.Never);
        _mockStreamResumeService.Verify(
            s => s.TryResumeStreamAsync(It.IsAny<StoredTopic>()),
            Times.Never);
    }

    public void Dispose()
    {
        _sut?.Dispose();
        _connectionStore.Dispose();
        _topicsStore.Dispose();
    }
}
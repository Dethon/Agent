using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class StreamServiceTests : IDisposable
{
    private readonly SessionService _sessionService = new();
    private readonly Mock<IPushNotificationService> _pushNotification = new();
    private readonly StreamService _sut;

    public StreamServiceTests()
    {
        _sut = new StreamService(
            _sessionService,
            _pushNotification.Object,
            new Mock<ILogger<StreamService>>().Object);
    }

    [Fact]
    public void GetOrCreateStream_CreatesAndReusesStream()
    {
        var (channel1, _) = _sut.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);
        channel1.ShouldNotBeNull();
        _sut.IsStreaming("topic1").ShouldBeTrue();

        var (channel2, _) = _sut.GetOrCreateStream("topic1", "p2", "u1", CancellationToken.None);
        channel1.ShouldBeSameAs(channel2);
    }

    [Fact]
    public async Task WriteMessageAsync_DeliversToSubscriber()
    {
        var (channel, _) = _sut.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);
        var reader = channel.Subscribe();

        await _sut.WriteMessageAsync("topic1", new ChatStreamMessage { Content = "hello" });

        (await reader.ReadAsync()).Content.ShouldBe("hello");
    }

    [Fact]
    public async Task WriteReplyAsync_TextContent_CreatesCorrectMessage()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        var (channel, _) = _sut.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);
        var reader = channel.Subscribe();

        await _sut.WriteReplyAsync(new SendReplyParams
        { ConversationId = "100:200", Content = "hello", ContentType = "text", IsComplete = false, MessageId = "msg-1" });

        var msg = await reader.ReadAsync();
        msg.Content.ShouldBe("hello");
        msg.MessageId.ShouldBe("msg-1");
    }

    [Fact]
    public async Task WriteReplyAsync_StreamComplete_CompletesStream()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        _sut.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

        await _sut.WriteReplyAsync(new SendReplyParams
        { ConversationId = "100:200", Content = "", ContentType = "stream_complete", IsComplete = true });

        _sut.IsStreaming("topic1").ShouldBeFalse();
    }

    [Fact]
    public void CancelStream_CancelsTokenAndCleansUp()
    {
        var (_, token) = _sut.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

        _sut.CancelStream("topic1");

        token.IsCancellationRequested.ShouldBeTrue();
        _sut.IsStreaming("topic1").ShouldBeFalse();
    }

    [Fact]
    public void TryIncrementPending_ReturnsBasedOnStreamExistence()
    {
        _sut.TryIncrementPending("nonexistent").ShouldBeFalse();

        _sut.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);
        _sut.TryIncrementPending("topic1").ShouldBeTrue();
    }

    [Fact]
    public void GetStreamState_ActiveStream_ReturnsState()
    {
        _sut.GetOrCreateStream("topic1", "my prompt", "user1", CancellationToken.None);

        var state = _sut.GetStreamState("topic1");
        state.ShouldNotBeNull();
        state.IsProcessing.ShouldBeTrue();
        state.CurrentPrompt.ShouldBe("my prompt");
    }

    [Fact]
    public void GetStreamState_NoStream_ReturnsNull()
    {
        _sut.GetStreamState("nonexistent").ShouldBeNull();
    }

    public void Dispose() => _sut.Dispose();
}
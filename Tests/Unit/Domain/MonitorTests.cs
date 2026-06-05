using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.Monitor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain;

internal sealed class FakeAiAgent : DisposableAgent
{
    public Exception? ExceptionToThrow { get; init; }

    public int WarmupCalls;
    public TaskCompletionSource WarmupSignaled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TimeSpan WarmupDelay { get; init; }
    public ConcurrentQueue<string> Events { get; } = new();
    public ConcurrentQueue<string> RestoredSessionKeys { get; } = new();

    public override async Task WarmupSessionAsync(AgentSession thread, CancellationToken ct = default)
    {
        Interlocked.Increment(ref WarmupCalls);
        if (WarmupDelay > TimeSpan.Zero)
        {
            await Task.Delay(WarmupDelay, ct);
        }

        Events.Enqueue("warmup");
        WarmupSignaled.TrySetResult();
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<AgentSession>(new FakeAgentThread());
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedThread,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (serializedThread.ValueKind == JsonValueKind.String && serializedThread.GetString() is { } key)
        {
            RestoredSessionKeys.Enqueue(key);
        }
        return ValueTask.FromResult<AgentSession>(new FakeAgentThread());
    }

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new AgentResponse());
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? thread = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        Events.Enqueue("run");
        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        yield break;
    }

    public override ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask DisposeThreadSessionAsync(AgentSession thread)
    {
        return ValueTask.CompletedTask;
    }

    private sealed class FakeAgentThread : AgentSession;
}

internal sealed class FakeAgentFactory(DisposableAgent agent) : IAgentFactory
{
    public DisposableAgent Create(AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler)
    {
        return agent;
    }

    public DisposableAgent CreateSubAgent(SubAgentDefinition definition, IToolApprovalHandler approvalHandler, string[] whitelistPatterns, string userId)
        => throw new NotImplementedException();
}

internal sealed class FakeChannelConnection : IChannelConnection
{
    private readonly Channel<ChannelMessage> _channel = System.Threading.Channels.Channel.CreateUnbounded<ChannelMessage>();

    public string ChannelId { get; init; } = "test-channel";

    public string? ConversationIdToReturn { get; init; }

    public Action<(string ConversationId, string Content, ReplyContentType ContentType, bool IsComplete)>? OnReply { get; init; }

    public IAsyncEnumerable<ChannelMessage> Messages => _channel.Reader.ReadAllAsync();

    public List<(string ConversationId, string Content, ReplyContentType ContentType, bool IsComplete)> SentReplies { get; } = [];

    public List<(string AgentId, string TopicName, string Sender, string? InitialPrompt)> CreatedConversations { get; } = [];

    public List<(string ConversationId, IReadOnlyList<ToolApprovalRequest> Requests)> NotifyAutoApprovedCalls { get; } = [];

    public Task SendReplyAsync(string conversationId, string content, ReplyContentType contentType, bool isComplete, string? messageId, CancellationToken ct)
    {
        var reply = (conversationId, content, contentType, isComplete);
        SentReplies.Add(reply);
        OnReply?.Invoke(reply);
        return Task.CompletedTask;
    }

    public Task<ToolApprovalResult> RequestApprovalAsync(string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct)
        => Task.FromResult(new ToolApprovalResult());

    public Task NotifyAutoApprovedAsync(string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct)
    {
        NotifyAutoApprovedCalls.Add((conversationId, requests));
        return Task.CompletedTask;
    }

    public Task<string?> CreateConversationAsync(string agentId, string topicName, string sender, string? initialPrompt, CancellationToken ct)
    {
        CreatedConversations.Add((agentId, topicName, sender, initialPrompt));
        return Task.FromResult(ConversationIdToReturn);
    }

    public void WriteMessage(ChannelMessage message) => _channel.Writer.TryWrite(message);

    public void Complete() => _channel.Writer.TryComplete();
}

internal static class MonitorTestMocks
{
    public static ChannelMessage CreateChannelMessage(
        string conversationId = "conv-1",
        string content = "Hello",
        string sender = "test",
        string channelId = "test-channel",
        string? agentId = null)
    {
        return new ChannelMessage
        {
            ConversationId = conversationId,
            Content = content,
            Sender = sender,
            ChannelId = channelId,
            AgentId = agentId
        };
    }

    public static FakeChannelConnection CreateChannel(string channelId = "test-channel", params ChannelMessage[] messages)
    {
        var channel = new FakeChannelConnection { ChannelId = channelId };
        foreach (var msg in messages)
        {
            channel.WriteMessage(msg);
        }
        channel.Complete();
        return channel;
    }

    public static FakeAiAgent CreateAgent()
    {
        return new FakeAiAgent();
    }

    public static IAgentFactory CreateAgentFactory(FakeAiAgent agent)
    {
        return new FakeAgentFactory(agent);
    }

    public static ChatThreadResolver CreateThreadResolver()
    {
        return new ChatThreadResolver();
    }

    public static Func<IChannelConnection, string, IToolApprovalHandler> CreateApprovalHandlerFactory()
    {
        return (_, _) => new Mock<IToolApprovalHandler>().Object;
    }
}

public class ChatMonitorTests
{
    [Fact]
    public async Task Monitor_SingleMessage_SendsStreamCompleteReply()
    {
        // Arrange
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var message = MonitorTestMocks.CreateChannelMessage();
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(
            [channel],
            agentFactory,
            MonitorTestMocks.CreateApprovalHandlerFactory(),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - at minimum a StreamComplete reply should be sent
        channel.SentReplies.ShouldContain(r =>
            r.ContentType == ReplyContentType.StreamComplete && r.IsComplete);
    }

    [Fact]
    public async Task Monitor_SingleMessage_WarmsUpSessionOncePerConversation()
    {
        // Arrange
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var message = MonitorTestMocks.CreateChannelMessage();
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);

        var monitor = new ChatMonitor(
            [channel],
            agentFactory,
            MonitorTestMocks.CreateApprovalHandlerFactory(),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        // Act
        await monitor.Monitor(CancellationToken.None);
        var done = await Task.WhenAny(fakeAgent.WarmupSignaled.Task, Task.Delay(TimeSpan.FromSeconds(2)));

        // Assert - the session is warmed up exactly once for the conversation
        done.ShouldBe(fakeAgent.WarmupSignaled.Task);
        fakeAgent.WarmupCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Monitor_AwaitsWarmupBeforeStreaming_Deterministically()
    {
        // Arrange - slow warmup; if it were fire-and-forget, streaming would start first
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var message = MonitorTestMocks.CreateChannelMessage();
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = new FakeAiAgent { WarmupDelay = TimeSpan.FromMilliseconds(150) };
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);

        var monitor = new ChatMonitor(
            [channel],
            agentFactory,
            MonitorTestMocks.CreateApprovalHandlerFactory(),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - warmup completes before the first streaming turn starts
        fakeAgent.Events.ToArray().ShouldBe(["warmup", "run"]);
    }

    [Fact]
    public async Task Monitor_MultipleChannels_RoutesRepliesToOriginatingChannel()
    {
        // Arrange
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var msg1 = MonitorTestMocks.CreateChannelMessage(conversationId: "conv-1", channelId: "ch-1");
        var msg2 = MonitorTestMocks.CreateChannelMessage(conversationId: "conv-2", channelId: "ch-2");
        var channel1 = MonitorTestMocks.CreateChannel("ch-1", msg1);
        var channel2 = MonitorTestMocks.CreateChannel("ch-2", msg2);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(
            [channel1, channel2],
            agentFactory,
            MonitorTestMocks.CreateApprovalHandlerFactory(),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - each channel should receive replies for its own conversation
        channel1.SentReplies.ShouldAllBe(r => r.ConversationId == "conv-1");
        channel2.SentReplies.ShouldAllBe(r => r.ConversationId == "conv-2");
    }

    [Fact]
    public async Task Monitor_SecondMessageFromDifferentChannelInSameConversation_RepliesToThatChannel()
    {
        // A voice-started conversation is mirrored into WebChat under the SAME
        // ConversationId, so both channels share AgentKey(ConversationId, AgentId) and
        // the WebChat message joins the existing voice group. The reply to a message
        // must follow the channel that actually sent it: when the user types into the
        // WebChat conversation, the answer belongs in WebChat, not spoken on the voice
        // satellite that originally opened it (which would also leave WebChat stuck
        // "streaming" because it never receives the terminal StreamComplete).
        var threadResolver = MonitorTestMocks.CreateThreadResolver();

        var completes = 0;
        var firstSpoken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void track((string ConversationId, string Content, ReplyContentType ContentType, bool IsComplete) r)
        {
            if (r.ContentType != ReplyContentType.StreamComplete)
            {
                return;
            }

            var n = Interlocked.Increment(ref completes);
            if (n == 1)
            {
                firstSpoken.TrySetResult();
            }
            else if (n == 2)
            {
                secondDelivered.TrySetResult();
            }
        }

        var voice = new FakeChannelConnection { ChannelId = "voice", OnReply = track };
        var webchat = new FakeChannelConnection { ChannelId = "webchat", OnReply = track };
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);

        var monitor = new ChatMonitor(
            [voice, webchat],
            agentFactory,
            MonitorTestMocks.CreateApprovalHandlerFactory(),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = monitor.Monitor(cts.Token);

        // User speaks: voice opens conversation "7:42".
        voice.WriteMessage(MonitorTestMocks.CreateChannelMessage(
            conversationId: "7:42", channelId: "voice", agentId: "jonas"));
        await firstSpoken.Task.WaitAsync(cts.Token);

        // User then types in the SAME conversation from WebChat.
        webchat.WriteMessage(MonitorTestMocks.CreateChannelMessage(
            conversationId: "7:42", channelId: "webchat", agentId: "jonas"));
        await secondDelivered.Task.WaitAsync(cts.Token);

        voice.Complete();
        webchat.Complete();
        await run;

        webchat.SentReplies.ShouldContain(r =>
            r.ContentType == ReplyContentType.StreamComplete && r.IsComplete);
    }

    [Fact]
    public async Task Monitor_WithCancelCommand_CancelsWithoutWipingThread()
    {
        // Arrange
        var mockStateStore = new Mock<IThreadStateStore>();
        var threadResolver = new ChatThreadResolver(mockStateStore.Object);
        var agentKey = new AgentKey("conv-1");
        var message = MonitorTestMocks.CreateChannelMessage(conversationId: "conv-1", content: "/cancel");
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        // Pre-resolve a context so we can verify cancellation
        var context = threadResolver.Resolve(agentKey);

        var monitor = new ChatMonitor(
            [channel],
            agentFactory,
            MonitorTestMocks.CreateApprovalHandlerFactory(),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - CTS should be canceled but thread state should NOT be deleted
        context.Cts.IsCancellationRequested.ShouldBeTrue();
        mockStateStore.Verify(s => s.DeleteAsync(It.IsAny<AgentKey>()), Times.Never);
    }

    [Fact]
    public async Task Monitor_AgentStreamThrows_PublishesErrorEvent()
    {
        // Arrange
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var message = MonitorTestMocks.CreateChannelMessage();
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = new FakeAiAgent { ExceptionToThrow = new HttpRequestException("422 Unprocessable Entity") };
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();
        var metricsPublisher = new Mock<IMetricsPublisher>();

        var monitor = new ChatMonitor(
            [channel],
            agentFactory,
            MonitorTestMocks.CreateApprovalHandlerFactory(),
            threadResolver,
            metricsPublisher.Object,
            null,
            logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert
        metricsPublisher.Verify(p => p.PublishAsync(
            It.Is<ErrorEvent>(e =>
                e.Service == "agent" &&
                e.ErrorType == nameof(HttpRequestException) &&
                e.Message.Contains("422 Unprocessable Entity")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Monitor_ScheduledFireWithReplyTo_BindsApprovalHandlerToDeliveryTarget()
    {
        // For a scheduled fire the origin channel (mcp-scheduling) auto-approves
        // every tool call silently. If the approval handler is bound to the origin,
        // the delivery target (WebChat) never sees the tool calls and the user
        // can't tell what the scheduled run did. The handler must instead bind to
        // the first delivery target so tool-call notifications surface there.
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var fire = MonitorTestMocks.CreateChannelMessage(
            conversationId: "fire-1", channelId: "scheduling", agentId: "jonas") with
        {
            ReplyTo = [new ReplyTarget("signalr", null)]
        };
        var scheduling = MonitorTestMocks.CreateChannel("scheduling", fire);
        var signalr = new FakeChannelConnection { ChannelId = "signalr", ConversationIdToReturn = "minted-signalr" };
        signalr.Complete();
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        (string ChannelId, string ConversationId)? captured = null;
        Func<IChannelConnection, string, IToolApprovalHandler> factory = (ch, cid) =>
        {
            captured = (ch.ChannelId, cid);
            return new Mock<IToolApprovalHandler>().Object;
        };

        var monitor = new ChatMonitor(
            [scheduling, signalr],
            agentFactory,
            factory,
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        await monitor.Monitor(CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.Value.ChannelId.ShouldBe("signalr");
        captured.Value.ConversationId.ShouldBe("minted-signalr");
    }

    [Fact]
    public async Task Monitor_WithClearCommand_CleansUpAndWipesThread()
    {
        // Arrange
        var mockStateStore = new Mock<IThreadStateStore>();
        mockStateStore.Setup(s => s.DeleteAsync(It.IsAny<AgentKey>())).Returns(Task.CompletedTask);
        var threadResolver = new ChatThreadResolver(mockStateStore.Object);
        var agentKey = new AgentKey("conv-1");
        var message = MonitorTestMocks.CreateChannelMessage(conversationId: "conv-1", content: "/clear");
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        // Pre-resolve a context so we can verify cleanup
        var context = threadResolver.Resolve(agentKey);

        var monitor = new ChatMonitor(
            [channel],
            agentFactory,
            MonitorTestMocks.CreateApprovalHandlerFactory(),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - CTS should be canceled AND thread state should be deleted
        context.Cts.IsCancellationRequested.ShouldBeTrue();
        mockStateStore.Verify(s => s.DeleteAsync(agentKey), Times.Once);
    }
}
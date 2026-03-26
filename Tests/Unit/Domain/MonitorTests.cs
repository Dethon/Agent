using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.DTOs.WebChat;
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

    public IReadOnlyList<AgentInfo> GetAvailableAgents(string? userId = null)
    {
        return [];
    }

    public AgentInfo RegisterCustomAgent(string userId, CustomAgentRegistration registration)
        => throw new NotImplementedException();

    public bool UnregisterCustomAgent(string userId, string agentId)
        => throw new NotImplementedException();

    public DisposableAgent CreateSubAgent(SubAgentDefinition definition, IToolApprovalHandler approvalHandler, string[] whitelistPatterns, string userId)
        => throw new NotImplementedException();
}

internal sealed class FakeChannelConnection : IChannelConnection
{
    private readonly Channel<ChannelMessage> _channel = Channel.CreateUnbounded<ChannelMessage>();

    public string ChannelId { get; init; } = "test-channel";

    public string? ConversationIdToReturn { get; init; }

    public IAsyncEnumerable<ChannelMessage> Messages => _channel.Reader.ReadAllAsync();

    public List<(string ConversationId, string Content, string ContentType, bool IsComplete)> SentReplies { get; } = [];

    public List<(string AgentId, string TopicName, string Sender)> CreatedConversations { get; } = [];

    public Task SendReplyAsync(string conversationId, string content, string contentType, bool isComplete, string? messageId, CancellationToken ct)
    {
        SentReplies.Add((conversationId, content, contentType, isComplete));
        return Task.CompletedTask;
    }

    public Task<ToolApprovalResult> RequestApprovalAsync(string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct)
        => Task.FromResult(new ToolApprovalResult());

    public Task NotifyAutoApprovedAsync(string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct)
        => Task.CompletedTask;

    public Task<string?> CreateConversationAsync(string agentId, string topicName, string sender, CancellationToken ct)
    {
        CreatedConversations.Add((agentId, topicName, sender));
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
            logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - at minimum a StreamComplete reply should be sent
        channel.SentReplies.ShouldContain(r =>
            r.ContentType == ReplyContentType.StreamComplete && r.IsComplete);
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
            logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - each channel should receive replies for its own conversation
        channel1.SentReplies.ShouldAllBe(r => r.ConversationId == "conv-1");
        channel2.SentReplies.ShouldAllBe(r => r.ConversationId == "conv-2");
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
            logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - CTS should be canceled AND thread state should be deleted
        context.Cts.IsCancellationRequested.ShouldBeTrue();
        mockStateStore.Verify(s => s.DeleteAsync(agentKey), Times.Once);
    }
}

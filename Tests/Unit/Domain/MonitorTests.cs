using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
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
    public override ValueTask<AgentSession> GetNewSessionAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<AgentSession>(new FakeAgentThread());
    }

    public override ValueTask<AgentSession> DeserializeSessionAsync(
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
    public DisposableAgent Create(AgentKey agentKey, string userId, string? agentId)
    {
        return agent;
    }

    public IReadOnlyList<AgentInfo> GetAvailableAgents()
    {
        return [];
    }
}

internal static class MonitorTestMocks
{
    public static ChatPrompt CreatePrompt(long chatId = 1, int? threadId = 1, string prompt = "Hello",
        int messageId = 1, string sender = "test")
    {
        return new ChatPrompt
        {
            ChatId = chatId,
            ThreadId = threadId,
            Prompt = prompt,
            MessageId = messageId,
            Sender = sender,
            Source = MessageSource.WebUi
        };
    }

    public static Mock<IChatMessengerClient> CreateChatMessengerClient(IEnumerable<ChatPrompt>? prompts = null)
    {
        var mock = new Mock<IChatMessengerClient>();
        if (prompts != null)
        {
            mock.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(prompts.ToAsyncEnumerable());
        }

        mock.Setup(c =>
                c.ProcessResponseStreamAsync(
                    It.IsAny<IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>>(),
                    It.IsAny<CancellationToken>()
                ))
            .Returns(async (IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
                CancellationToken ct) =>
            {
                // Must consume the enumerable to drive the lazy streaming pipeline
                await foreach (var _ in updates.WithCancellation(ct)) { }
            });
        return mock;
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
}

public class ChatMonitorTests
{
    [Fact]
    public async Task Monitor_WhenAgentCompletes_CallsProcessResponseStreamAsync()
    {
        // Arrange
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var prompts = new[] { MonitorTestMocks.CreatePrompt() };
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient(prompts);
        chatMessengerClient.Setup(c =>
                c.CreateTopicIfNeededAsync(It.IsAny<MessageSource>(), It.IsAny<long?>(), It.IsAny<long?>(),
                    It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentKey(1, 1));

        var mockAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(mockAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(chatMessengerClient.Object, agentFactory, threadResolver, logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - ProcessResponseStreamAsync should be called
        chatMessengerClient.Verify(c =>
            c.ProcessResponseStreamAsync(
                It.IsAny<IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>>(),
                It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Monitor_WithNullThreadId_CallsCreateTopicIfNeededAsync()
    {
        // Arrange
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var prompts = new[] { MonitorTestMocks.CreatePrompt(threadId: null) };
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient(prompts);
        chatMessengerClient.Setup(c =>
                c.CreateTopicIfNeededAsync(It.IsAny<MessageSource>(), It.IsAny<long?>(), It.IsAny<long?>(),
                    It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentKey(1, 100));
        var mockAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(mockAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(chatMessengerClient.Object, agentFactory, threadResolver, logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - CreateTopicIfNeededAsync is called with prompt parameters
        chatMessengerClient.Verify(
            c => c.CreateTopicIfNeededAsync(MessageSource.WebUi, 1, null, null, "Hello", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Monitor_WithCancelCommand_CancelsWithoutWipingThread()
    {
        // Arrange
        var mockStateStore = new Mock<IThreadStateStore>();
        var threadResolver = new ChatThreadResolver(mockStateStore.Object);
        var prompts = new[] { MonitorTestMocks.CreatePrompt(prompt: "/cancel") };
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient(prompts);
        var agentKey = new AgentKey(1, 1);
        chatMessengerClient.Setup(c =>
                c.CreateTopicIfNeededAsync(It.IsAny<MessageSource>(), It.IsAny<long?>(), It.IsAny<long?>(),
                    It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(agentKey);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        // First resolve a context for the agent key so we can verify it gets canceled but not cleaned
        var context = threadResolver.Resolve(agentKey);

        var monitor = new ChatMonitor(chatMessengerClient.Object, agentFactory, threadResolver, logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - CTS should be canceled but thread state should NOT be deleted
        context.Cts.IsCancellationRequested.ShouldBeTrue();
        mockStateStore.Verify(s => s.DeleteAsync(It.IsAny<AgentKey>()), Times.Never);
    }

    [Fact]
    public async Task Monitor_WithClearCommand_CleansUpAndWipesThread()
    {
        // Arrange
        var mockStateStore = new Mock<IThreadStateStore>();
        mockStateStore.Setup(s => s.DeleteAsync(It.IsAny<AgentKey>())).Returns(Task.CompletedTask);
        var threadResolver = new ChatThreadResolver(mockStateStore.Object);
        var prompts = new[] { MonitorTestMocks.CreatePrompt(prompt: "/clear") };
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient(prompts);
        var agentKey = new AgentKey(1, 1);
        chatMessengerClient.Setup(c =>
                c.CreateTopicIfNeededAsync(It.IsAny<MessageSource>(), It.IsAny<long?>(), It.IsAny<long?>(),
                    It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(agentKey);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        // First resolve a context for the agent key so we can verify it gets cleaned
        var context = threadResolver.Resolve(agentKey);

        var monitor = new ChatMonitor(chatMessengerClient.Object, agentFactory, threadResolver, logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - CTS should be canceled AND thread state should be deleted
        context.Cts.IsCancellationRequested.ShouldBeTrue();
        mockStateStore.Verify(s => s.DeleteAsync(agentKey), Times.Once);
    }
}
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
    public override ValueTask<AgentThread> GetNewThreadAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<AgentThread>(new FakeAgentThread());
    }

    public override ValueTask<AgentThread> DeserializeThreadAsync(
        JsonElement serializedThread,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<AgentThread>(new FakeAgentThread());
    }

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new AgentResponse());
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
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

    public override ValueTask DisposeThreadSessionAsync(AgentThread thread)
    {
        return ValueTask.CompletedTask;
    }

    private sealed class FakeAgentThread : AgentThread;
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
            Sender = sender
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
                    It.IsAny<IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)>>(),
                    It.IsAny<CancellationToken>()
                ))
            .Returns(async (IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
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

        var mockAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(mockAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(chatMessengerClient.Object, agentFactory, threadResolver, logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - ProcessResponseStreamAsync should be called
        chatMessengerClient.Verify(c =>
            c.ProcessResponseStreamAsync(
                It.IsAny<IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)>>(),
                It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Monitor_WithNullThreadId_CallsCreateThread()
    {
        // Arrange
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var prompts = new[] { MonitorTestMocks.CreatePrompt(threadId: null) };
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient(prompts);
        chatMessengerClient.Setup(c =>
                c.CreateThread(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(100);
        var mockAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(mockAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(chatMessengerClient.Object, agentFactory, threadResolver, logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - CreateThread is called during Monitor, not during task processing
        chatMessengerClient.Verify(c => c.CreateThread(1, "Hello", It.IsAny<string?>(), It.IsAny<CancellationToken>()),
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
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        // First resolve a context for the agent key so we can verify it gets canceled but not cleaned
        var agentKey = new AgentKey(1, 1);
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
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        // First resolve a context for the agent key so we can verify it gets cleaned
        var agentKey = new AgentKey(1, 1);
        var context = threadResolver.Resolve(agentKey);

        var monitor = new ChatMonitor(chatMessengerClient.Object, agentFactory, threadResolver, logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - CTS should be canceled AND thread state should be deleted
        context.Cts.IsCancellationRequested.ShouldBeTrue();
        mockStateStore.Verify(s => s.DeleteAsync(agentKey), Times.Once);
    }
}

public class AgentCleanupMonitorTests
{
    [Fact]
    public async Task Check_WithExistingChannel_DoesNotCleanup()
    {
        // Arrange
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient();

        threadResolver.Resolve(new AgentKey(1, 1));

        chatMessengerClient.Setup(c => c.DoesThreadExist(1, 1, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var monitor = new AgentCleanupMonitor(threadResolver, chatMessengerClient.Object);

        // Act
        await monitor.Check(CancellationToken.None);

        // Assert
        threadResolver.AgentKeys.Count().ShouldBe(1);
    }

    [Fact]
    public async Task Check_WithDeletedThread_CleansUpChannel()
    {
        // Arrange
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient();

        threadResolver.Resolve(new AgentKey(1, 1));

        chatMessengerClient.Setup(c => c.DoesThreadExist(1, 1, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var monitor = new AgentCleanupMonitor(threadResolver, chatMessengerClient.Object);

        // Act
        await monitor.Check(CancellationToken.None);

        // Assert
        threadResolver.AgentKeys.Count().ShouldBe(0);
    }

    [Fact]
    public async Task Check_WithMultipleChannels_CleansUpOnlyDeletedThreads()
    {
        // Arrange
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient();

        threadResolver.Resolve(new AgentKey(1, 1));
        threadResolver.Resolve(new AgentKey(2, 2));

        chatMessengerClient.Setup(c => c.DoesThreadExist(1, 1, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        chatMessengerClient.Setup(c => c.DoesThreadExist(2, 2, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var monitor = new AgentCleanupMonitor(threadResolver, chatMessengerClient.Object);

        // Act
        await monitor.Check(CancellationToken.None);

        // Assert
        threadResolver.AgentKeys.Count().ShouldBe(1);
        threadResolver.AgentKeys.ShouldContain(x => x.ChatId == 1 && x.ThreadId == 1);
    }

    [Fact]
    public async Task Check_WithNoChannels_DoesNothing()
    {
        // Arrange
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient();

        var monitor = new AgentCleanupMonitor(threadResolver, chatMessengerClient.Object);

        // Act & Assert - should not throw
        await Should.NotThrowAsync(async () => await monitor.Check(CancellationToken.None));

        chatMessengerClient.Verify(
            c => c.DoesThreadExist(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Monitor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Integration.Domain;

internal sealed class FakeCancellableAiAgent : CancellableAiAgent
{
    public int CancelCallCount { get; private set; }
    public int DisposeCallCount { get; private set; }

    public override void CancelCurrentExecution()
    {
        CancelCallCount++;
    }

    public override ValueTask DisposeAsync()
    {
        DisposeCallCount++;
        return ValueTask.CompletedTask;
    }

    public override AgentThread GetNewThread()
    {
        return new FakeAgentThread();
    }

    public override AgentThread
        DeserializeThread(JsonElement serializedThread, JsonSerializerOptions? options = null)
    {
        return new FakeAgentThread();
    }

    public override Task<AgentRunResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new AgentRunResponse());
    }

    public override async IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class FakeAgentThread : AgentThread;
}

internal static class MonitorTestMocks
{
    public static ChatPrompt CreatePrompt(long chatId = 1, int? threadId = 1, string prompt = "Hello",
        bool isCommand = false, int messageId = 1, string sender = "test")
    {
        return new ChatPrompt
        {
            ChatId = chatId,
            ThreadId = threadId,
            Prompt = prompt,
            IsCommand = isCommand,
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
                c.BlockWhile(It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<Func<CancellationToken, Task>>(),
                    It.IsAny<CancellationToken>()))
            .Returns<long, long?, Func<CancellationToken, Task>, CancellationToken>((_, _, task, ct) => task(ct));
        mock.Setup(c => c.SendResponse(It.IsAny<long>(), It.IsAny<ChatResponseMessage>(), It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    public static FakeCancellableAiAgent CreateAgent()
    {
        return new FakeCancellableAiAgent();
    }

    public static Mock<IMcpAgentFactory> CreateAgentFactory(FakeCancellableAiAgent agent)
    {
        var mock = new Mock<IMcpAgentFactory>();
        mock.Setup(f => f.Create(It.IsAny<Func<AiResponse, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        return mock;
    }
}

public class ChatMonitorTests
{
    [Fact]
    public async Task Monitor_WithPrompt_QueuesAgentTask()
    {
        // Arrange
        var agentResolver = new AgentResolver();
        var queue = new TaskQueue();
        var prompts = new[] { MonitorTestMocks.CreatePrompt() };
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient(prompts);
        var mockAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(mockAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(agentResolver, queue, chatMessengerClient.Object, agentFactory.Object,
            logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert
        queue.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Monitor_WithNullThreadId_CreatesThread()
    {
        // Arrange
        var agentResolver = new AgentResolver();
        var queue = new TaskQueue();
        var prompts = new[] { MonitorTestMocks.CreatePrompt(threadId: null) };
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient(prompts);
        chatMessengerClient.Setup(c =>
                c.CreateThread(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(100);
        var mockAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(mockAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(agentResolver, queue, chatMessengerClient.Object, agentFactory.Object,
            logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Process the queued task
        var task = await queue.DequeueTask(CancellationToken.None);
        await task(CancellationToken.None);

        // Assert
        chatMessengerClient.Verify(c => c.CreateThread(1, "Hello", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Monitor_WithCommand_CancelsCurrentExecution()
    {
        // Arrange
        var agentResolver = new AgentResolver();
        var queue = new TaskQueue();
        var prompts = new[] { MonitorTestMocks.CreatePrompt(prompt: "cancel", isCommand: true) };
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient(prompts);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(
            agentResolver, queue, chatMessengerClient.Object, agentFactory.Object, logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Process the queued task
        var task = await queue.DequeueTask(CancellationToken.None);
        await task(CancellationToken.None);

        // Assert
        fakeAgent.CancelCallCount.ShouldBe(1);
    }
}

public class AgentCleanupMonitorTests
{
    [Fact]
    public async Task Check_WithExistingThread_DoesNotCleanup()
    {
        // Arrange
        var agentResolver = new AgentResolver();
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient();
        var fakeAgent = MonitorTestMocks.CreateAgent();

        await agentResolver.Resolve(1, 1, _ => Task.FromResult<CancellableAiAgent>(fakeAgent), CancellationToken.None);

        chatMessengerClient.Setup(c => c.DoesThreadExist(1, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var monitor = new AgentCleanupMonitor(agentResolver, chatMessengerClient.Object);

        // Act
        await monitor.Check(CancellationToken.None);

        // Assert
        agentResolver.Agents.Length.ShouldBe(1);
        fakeAgent.DisposeCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Check_WithDeletedThread_CleansUpAgent()
    {
        // Arrange
        var agentResolver = new AgentResolver();
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient();
        var fakeAgent = MonitorTestMocks.CreateAgent();

        await agentResolver.Resolve(1, 1, _ => Task.FromResult<CancellableAiAgent>(fakeAgent), CancellationToken.None);

        chatMessengerClient.Setup(c => c.DoesThreadExist(1, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var monitor = new AgentCleanupMonitor(agentResolver, chatMessengerClient.Object);

        // Act
        await monitor.Check(CancellationToken.None);

        // Assert
        agentResolver.Agents.Length.ShouldBe(0);
        fakeAgent.DisposeCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Check_WithMultipleAgents_CleansUpOnlyDeletedThreads()
    {
        // Arrange
        var agentResolver = new AgentResolver();
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient();
        var fakeAgent1 = MonitorTestMocks.CreateAgent();
        var fakeAgent2 = MonitorTestMocks.CreateAgent();

        await agentResolver.Resolve(1, 1, _ => Task.FromResult<CancellableAiAgent>(fakeAgent1), CancellationToken.None);
        await agentResolver.Resolve(2, 2, _ => Task.FromResult<CancellableAiAgent>(fakeAgent2), CancellationToken.None);

        chatMessengerClient.Setup(c => c.DoesThreadExist(1, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        chatMessengerClient.Setup(c => c.DoesThreadExist(2, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var monitor = new AgentCleanupMonitor(agentResolver, chatMessengerClient.Object);

        // Act
        await monitor.Check(CancellationToken.None);

        // Assert
        agentResolver.Agents.Length.ShouldBe(1);
        agentResolver.Agents.ShouldContain(x => x.ChatId == 1 && x.ThreadId == 1);
        fakeAgent1.DisposeCallCount.ShouldBe(0);
        fakeAgent2.DisposeCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Check_WithNoAgents_DoesNothing()
    {
        // Arrange
        var agentResolver = new AgentResolver();
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient();

        var monitor = new AgentCleanupMonitor(agentResolver, chatMessengerClient.Object);

        // Act & Assert - should not throw
        await Should.NotThrowAsync(async () => await monitor.Check(CancellationToken.None));

        chatMessengerClient.Verify(
            c => c.DoesThreadExist(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
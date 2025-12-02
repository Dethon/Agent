using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Monitor;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Integration.Domain;

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
                c.BlockWhile(It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<Func<Task>>(),
                    It.IsAny<CancellationToken>()))
            .Returns<long, long?, Func<Task>, CancellationToken>((_, _, task, _) => task());
        mock.Setup(c => c.SendResponse(It.IsAny<long>(), It.IsAny<ChatResponseMessage>(), It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    public static Mock<IAgent> CreateAgent()
    {
        var mock = new Mock<IAgent>();
        mock.Setup(a => a.Run(It.IsAny<string[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mock.Setup(a => a.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return mock;
    }

    public static Mock<IAgentFactory> CreateAgentFactory(Mock<IAgent> agent)
    {
        var mock = new Mock<IAgentFactory>();
        mock.Setup(f => f.Create(It.IsAny<Func<AiResponse, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent.Object);
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
        var prompts = new[] { MonitorTestMocks.CreatePrompt(prompt: "/cancel", isCommand: true) };
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient(prompts);
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
        mockAgent.Verify(a => a.CancelCurrentExecution(), Times.Once);
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
        var mockAgent = MonitorTestMocks.CreateAgent();

        await agentResolver.Resolve(1, 1, _ => Task.FromResult(mockAgent.Object), CancellationToken.None);

        chatMessengerClient.Setup(c => c.DoesThreadExist(1, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var monitor = new AgentCleanupMonitor(agentResolver, chatMessengerClient.Object);

        // Act
        await monitor.Check(CancellationToken.None);

        // Assert
        agentResolver.Agents.Length.ShouldBe(1);
        mockAgent.Verify(a => a.DisposeAsync(), Times.Never);
    }

    [Fact]
    public async Task Check_WithDeletedThread_CleansUpAgent()
    {
        // Arrange
        var agentResolver = new AgentResolver();
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient();
        var mockAgent = MonitorTestMocks.CreateAgent();

        await agentResolver.Resolve(1, 1, _ => Task.FromResult(mockAgent.Object), CancellationToken.None);

        chatMessengerClient.Setup(c => c.DoesThreadExist(1, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var monitor = new AgentCleanupMonitor(agentResolver, chatMessengerClient.Object);

        // Act
        await monitor.Check(CancellationToken.None);

        // Assert
        agentResolver.Agents.Length.ShouldBe(0);
        mockAgent.Verify(a => a.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Check_WithMultipleAgents_CleansUpOnlyDeletedThreads()
    {
        // Arrange
        var agentResolver = new AgentResolver();
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient();
        var mockAgent1 = MonitorTestMocks.CreateAgent();
        var mockAgent2 = MonitorTestMocks.CreateAgent();

        await agentResolver.Resolve(1, 1, _ => Task.FromResult(mockAgent1.Object), CancellationToken.None);
        await agentResolver.Resolve(2, 2, _ => Task.FromResult(mockAgent2.Object), CancellationToken.None);

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
        mockAgent1.Verify(a => a.DisposeAsync(), Times.Never);
        mockAgent2.Verify(a => a.DisposeAsync(), Times.Once);
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
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Monitor;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Integration.Domain;

public class ChatMonitorTests
{
    [Fact]
    public async Task Monitor_WithPrompt_QueuesAgentTask()
    {
        // Arrange
        var agentResolver = new AgentResolver();
        var queue = new TaskQueue();
        var chatMessengerClient = new Mock<IChatMessengerClient>();
        var agentFactory = new Mock<IAgentFactory>();
        var logger = new Mock<ILogger<ChatMonitor>>();

        var prompts = new List<ChatPrompt>
        {
            new()
            {
                ChatId = 1,
                ThreadId = 1,
                Prompt = "Hello",
                IsCommand = false,
                MessageId = 1,
                Sender = "test"
            }
        };

        chatMessengerClient
            .Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(prompts.ToAsyncEnumerable());

        chatMessengerClient
            .Setup(c => c.BlockWhile(It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<Func<Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns<long, long?, Func<Task>, CancellationToken>((_, _, task, _) => task());

        var mockAgent = new Mock<IAgent>();
        mockAgent.Setup(a => a.Run(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        agentFactory
            .Setup(f => f.Create(It.IsAny<Func<AiResponse, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockAgent.Object);

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
        var chatMessengerClient = new Mock<IChatMessengerClient>();
        var agentFactory = new Mock<IAgentFactory>();
        var logger = new Mock<ILogger<ChatMonitor>>();

        var prompts = new List<ChatPrompt>
        {
            new()
            {
                ChatId = 1,
                ThreadId = null,
                Prompt = "Hello",
                IsCommand = false,
                MessageId = 1,
                Sender = "test"
            }
        };

        chatMessengerClient
            .Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(prompts.ToAsyncEnumerable());

        chatMessengerClient
            .Setup(c => c.CreateThread(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(100);

        chatMessengerClient
            .Setup(c => c.SendResponse(It.IsAny<long>(), It.IsAny<ChatResponseMessage>(), It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        chatMessengerClient
            .Setup(c => c.BlockWhile(It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<Func<Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns<long, long?, Func<Task>, CancellationToken>((_, _, task, _) => task());

        var mockAgent = new Mock<IAgent>();
        mockAgent.Setup(a => a.Run(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        agentFactory
            .Setup(f => f.Create(It.IsAny<Func<AiResponse, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockAgent.Object);

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
        var chatMessengerClient = new Mock<IChatMessengerClient>();
        var agentFactory = new Mock<IAgentFactory>();
        var logger = new Mock<ILogger<ChatMonitor>>();

        var prompts = new List<ChatPrompt>
        {
            new()
            {
                ChatId = 1,
                ThreadId = 1,
                Prompt = "/cancel",
                IsCommand = true,
                MessageId = 1,
                Sender = "test"
            }
        };

        chatMessengerClient
            .Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(prompts.ToAsyncEnumerable());

        chatMessengerClient
            .Setup(c => c.BlockWhile(It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<Func<Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns<long, long?, Func<Task>, CancellationToken>((_, _, task, _) => task());

        var mockAgent = new Mock<IAgent>();
        mockAgent.Setup(a => a.Run(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        agentFactory
            .Setup(f => f.Create(It.IsAny<Func<AiResponse, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockAgent.Object);

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

    [Fact]
    public async Task Monitor_WithException_LogsError()
    {
        // Arrange
        var agentResolver = new AgentResolver();
        var queue = new TaskQueue();
        var chatMessengerClient = new Mock<IChatMessengerClient>();
        var agentFactory = new Mock<IAgentFactory>();
        var logger = new Mock<ILogger<ChatMonitor>>();

        chatMessengerClient
            .Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Throws(new Exception("Test exception"));

        var monitor = new ChatMonitor(agentResolver, queue, chatMessengerClient.Object, agentFactory.Object,
            logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - should not throw, just log
        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}

public class AgentCleanupMonitorTests
{
    [Fact]
    public async Task Check_WithExistingThread_DoesNotCleanup()
    {
        // Arrange
        var agentResolver = new AgentResolver();
        var chatMessengerClient = new Mock<IChatMessengerClient>();

        var mockAgent = new Mock<IAgent>();
        mockAgent.Setup(a => a.DisposeAsync()).Returns(ValueTask.CompletedTask);

        await agentResolver.Resolve(1, 1, _ => Task.FromResult(mockAgent.Object), CancellationToken.None);

        chatMessengerClient
            .Setup(c => c.DoesThreadExist(1, 1, It.IsAny<CancellationToken>()))
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
        var chatMessengerClient = new Mock<IChatMessengerClient>();

        var mockAgent = new Mock<IAgent>();
        mockAgent.Setup(a => a.DisposeAsync()).Returns(ValueTask.CompletedTask);

        await agentResolver.Resolve(1, 1, _ => Task.FromResult(mockAgent.Object), CancellationToken.None);

        chatMessengerClient
            .Setup(c => c.DoesThreadExist(1, 1, It.IsAny<CancellationToken>()))
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
        var chatMessengerClient = new Mock<IChatMessengerClient>();

        var mockAgent1 = new Mock<IAgent>();
        var mockAgent2 = new Mock<IAgent>();
        mockAgent1.Setup(a => a.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mockAgent2.Setup(a => a.DisposeAsync()).Returns(ValueTask.CompletedTask);

        await agentResolver.Resolve(1, 1, _ => Task.FromResult(mockAgent1.Object), CancellationToken.None);
        await agentResolver.Resolve(2, 2, _ => Task.FromResult(mockAgent2.Object), CancellationToken.None);

        chatMessengerClient
            .Setup(c => c.DoesThreadExist(1, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        chatMessengerClient
            .Setup(c => c.DoesThreadExist(2, 2, It.IsAny<CancellationToken>()))
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
        var chatMessengerClient = new Mock<IChatMessengerClient>();

        var monitor = new AgentCleanupMonitor(agentResolver, chatMessengerClient.Object);

        // Act & Assert - should not throw
        await Should.NotThrowAsync(async () => await monitor.Check(CancellationToken.None));

        chatMessengerClient.Verify(
            c => c.DoesThreadExist(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
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
        mock.Setup(f => f.Create(
                It.IsAny<string>(),
                It.IsAny<Func<AiResponse, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
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
        var queue = new TaskQueue();
        var tracker = new RunningAgentTracker();
        var prompts = new[] { MonitorTestMocks.CreatePrompt() };
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient(prompts);
        var mockAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(mockAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(queue, tracker, chatMessengerClient.Object, agentFactory.Object, logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert
        queue.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Monitor_WithNullThreadId_CreatesThread()
    {
        // Arrange
        var queue = new TaskQueue();
        var tracker = new RunningAgentTracker();
        var prompts = new[] { MonitorTestMocks.CreatePrompt(threadId: null) };
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient(prompts);
        chatMessengerClient.Setup(c =>
                c.CreateThread(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(100);
        var mockAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(mockAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(queue, tracker, chatMessengerClient.Object, agentFactory.Object, logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Process the queued task
        var task = await queue.DequeueTask(CancellationToken.None);
        await task(CancellationToken.None);

        // Assert
        chatMessengerClient.Verify(c => c.CreateThread(1, "Hello", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Monitor_WithException_LogsError()
    {
        // Arrange
        var queue = new TaskQueue();
        var tracker = new RunningAgentTracker();
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient();
        chatMessengerClient.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Throws(new Exception("Test exception"));
        var mockAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(mockAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(queue, tracker, chatMessengerClient.Object, agentFactory.Object, logger.Object);

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

    [Fact]
    public async Task Monitor_WithPrompt_DisposesAgentAfterRun()
    {
        // Arrange
        var queue = new TaskQueue();
        var tracker = new RunningAgentTracker();
        var prompts = new[] { MonitorTestMocks.CreatePrompt() };
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient(prompts);
        var mockAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(mockAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(queue, tracker, chatMessengerClient.Object, agentFactory.Object, logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Process the queued task
        var task = await queue.DequeueTask(CancellationToken.None);
        await task(CancellationToken.None);

        // Assert - agent should be disposed after task completes
        mockAgent.Verify(a => a.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Monitor_WithCommand_CancelsRunningAgent()
    {
        // Arrange
        var queue = new TaskQueue();
        var tracker = new RunningAgentTracker();
        var prompts = new[] { MonitorTestMocks.CreatePrompt(isCommand: true) };
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient(prompts);
        var mockAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(mockAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(queue, tracker, chatMessengerClient.Object, agentFactory.Object, logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Process the queued task
        var task = await queue.DequeueTask(CancellationToken.None);
        await task(CancellationToken.None);

        // Assert - command should not create an agent
        agentFactory.Verify(
            f => f.Create(It.IsAny<string>(), It.IsAny<Func<AiResponse, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
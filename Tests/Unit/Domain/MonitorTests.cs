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

namespace Tests.Unit.Domain;

internal sealed class FakeAiAgent : DisposableAgent
{
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

    public override ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private sealed class FakeAgentThread : AgentThread;
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
                c.BlockWhile(It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<Func<CancellationToken, Task>>(),
                    It.IsAny<CancellationToken>()))
            .Returns<long, long?, Func<CancellationToken, Task>, CancellationToken>((_, _, task, ct) => task(ct));
        mock.Setup(c => c.SendResponse(It.IsAny<long>(), It.IsAny<ChatResponseMessage>(), It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    public static FakeAiAgent CreateAgent()
    {
        return new FakeAiAgent();
    }

    public static Func<AgentKey, CancellationToken, Task<DisposableAgent>> CreateAgentFactory(FakeAiAgent agent)
    {
        return (_, _) => Task.FromResult<DisposableAgent>(agent);
    }
}

public class ChatMonitorTests
{
    [Fact]
    public async Task Monitor_WithPrompt_QueuesAgentTask()
    {
        // Arrange
        var channelResolver = new ChannelResolver();
        var cancellationResolver = new CancellationResolver();
        var queue = new TaskQueue();
        var prompts = new[] { MonitorTestMocks.CreatePrompt() };
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient(prompts);
        var mockAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(mockAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(queue, chatMessengerClient.Object, agentFactory, channelResolver,
            cancellationResolver, logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert
        queue.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Monitor_WithNullThreadId_CallsCreateThread()
    {
        // Arrange
        var channelResolver = new ChannelResolver();
        var cancellationResolver = new CancellationResolver();
        var queue = new TaskQueue();
        var prompts = new[] { MonitorTestMocks.CreatePrompt(threadId: null) };
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient(prompts);
        chatMessengerClient.Setup(c =>
                c.CreateThread(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(100);
        var mockAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(mockAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(queue, chatMessengerClient.Object, agentFactory, channelResolver,
            cancellationResolver, logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - CreateThread is called during Monitor, not during task processing
        chatMessengerClient.Verify(c => c.CreateThread(1, "Hello", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Monitor_WithCancelCommand_CleansUpResources()
    {
        // Arrange
        var channelResolver = new ChannelResolver();
        var cancellationResolver = new CancellationResolver();
        var queue = new TaskQueue();
        var prompts = new[] { MonitorTestMocks.CreatePrompt(prompt: "/cancel") };
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient(prompts);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        // First resolve a CTS for the agent key so we can verify it gets cleaned
        var agentKey = new AgentKey(1, 1);
        var cts = cancellationResolver.Resolve(agentKey);
        channelResolver.Resolve(agentKey);

        var monitor = new ChatMonitor(queue, chatMessengerClient.Object, agentFactory, channelResolver,
            cancellationResolver, logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - the resources should be cleaned up (CTS gets cancelled and disposed when Clean is called)
        cts.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task Monitor_WithMultiplePromptsToSameThread_OnlyQueuesOnce()
    {
        // Arrange
        var channelResolver = new ChannelResolver();
        var cancellationResolver = new CancellationResolver();
        var queue = new TaskQueue();
        var prompts = new[]
        {
            MonitorTestMocks.CreatePrompt(prompt: "First"),
            MonitorTestMocks.CreatePrompt(prompt: "Second"),
            MonitorTestMocks.CreatePrompt(prompt: "Third")
        };
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient(prompts);
        var mockAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(mockAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(queue, chatMessengerClient.Object, agentFactory, channelResolver,
            cancellationResolver, logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - only one task queued since all prompts go to the same thread
        queue.Count.ShouldBe(1);
    }
}

public class AgentCleanupMonitorTests
{
    [Fact]
    public async Task Check_WithExistingChannel_DoesNotCleanup()
    {
        // Arrange
        var channelResolver = new ChannelResolver();
        var cancellationResolver = new CancellationResolver();
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient();

        channelResolver.Resolve(new AgentKey(1, 1));

        chatMessengerClient.Setup(c => c.DoesThreadExist(1, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var monitor = new AgentCleanupMonitor(channelResolver, cancellationResolver, chatMessengerClient.Object);

        // Act
        await monitor.Check(CancellationToken.None);

        // Assert
        channelResolver.AgentKeys.Count().ShouldBe(1);
    }

    [Fact]
    public async Task Check_WithDeletedThread_CleansUpChannel()
    {
        // Arrange
        var channelResolver = new ChannelResolver();
        var cancellationResolver = new CancellationResolver();
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient();

        channelResolver.Resolve(new AgentKey(1, 1));

        chatMessengerClient.Setup(c => c.DoesThreadExist(1, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var monitor = new AgentCleanupMonitor(channelResolver, cancellationResolver, chatMessengerClient.Object);

        // Act
        await monitor.Check(CancellationToken.None);

        // Assert
        channelResolver.AgentKeys.Count().ShouldBe(0);
    }

    [Fact]
    public async Task Check_WithMultipleChannels_CleansUpOnlyDeletedThreads()
    {
        // Arrange
        var channelResolver = new ChannelResolver();
        var cancellationResolver = new CancellationResolver();
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient();

        channelResolver.Resolve(new AgentKey(1, 1));
        channelResolver.Resolve(new AgentKey(2, 2));

        chatMessengerClient.Setup(c => c.DoesThreadExist(1, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        chatMessengerClient.Setup(c => c.DoesThreadExist(2, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var monitor = new AgentCleanupMonitor(channelResolver, cancellationResolver, chatMessengerClient.Object);

        // Act
        await monitor.Check(CancellationToken.None);

        // Assert
        channelResolver.AgentKeys.Count().ShouldBe(1);
        channelResolver.AgentKeys.ShouldContain(x => x.ChatId == 1 && x.ThreadId == 1);
    }

    [Fact]
    public async Task Check_WithNoChannels_DoesNothing()
    {
        // Arrange
        var channelResolver = new ChannelResolver();
        var cancellationResolver = new CancellationResolver();
        var chatMessengerClient = MonitorTestMocks.CreateChatMessengerClient();

        var monitor = new AgentCleanupMonitor(channelResolver, cancellationResolver, chatMessengerClient.Object);

        // Act & Assert - should not throw
        await Should.NotThrowAsync(async () => await monitor.Check(CancellationToken.None));

        chatMessengerClient.Verify(
            c => c.DoesThreadExist(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
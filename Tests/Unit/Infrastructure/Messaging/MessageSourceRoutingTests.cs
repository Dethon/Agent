using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Messaging;

public class MessageSourceRoutingTests
{
    [Fact]
    public async Task ProcessResponseStreamAsync_WebUiClientReceivesAllResponses()
    {
        // Arrange
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();
        var serviceBusUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus, serviceBusUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, serviceBusClient.Object]);

        // Simulate reading a prompt from ServiceBus to establish source mapping
        var serviceBusPrompt = new ChatPrompt
        {
            Prompt = "Hello from ServiceBus",
            ChatId = 123,
            ThreadId = 1,
            MessageId = 1,
            Sender = "user",
            Source = MessageSource.ServiceBus
        };

        serviceBusClient.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { serviceBusPrompt }.ToAsyncEnumerable());
        webUiClient.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<ChatPrompt>());

        // Read prompts to establish source tracking
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var _ in composite.ReadPrompts(100, cts.Token).Take(1)) { }

        // Create response for the ServiceBus prompt
        var response = (
            new AgentKey(123, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Response")] },
            (AiResponse?)null);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { response }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert - WebUI should receive the response (universal viewer)
        webUiUpdates.Count.ShouldBe(1);
        // ServiceBus should also receive it (source matches)
        serviceBusUpdates.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_ServiceBusClientDoesNotReceiveWebUiResponses()
    {
        // Arrange
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();
        var serviceBusUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus, serviceBusUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, serviceBusClient.Object]);

        // Simulate reading a prompt from WebUI
        var webUiPrompt = new ChatPrompt
        {
            Prompt = "Hello from WebUI",
            ChatId = 456,
            ThreadId = 1,
            MessageId = 1,
            Sender = "user",
            Source = MessageSource.WebUi
        };

        webUiClient.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { webUiPrompt }.ToAsyncEnumerable());
        serviceBusClient.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<ChatPrompt>());

        // Read prompts to establish source tracking
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var _ in composite.ReadPrompts(100, cts.Token).Take(1)) { }

        // Create response for the WebUI prompt
        var response = (
            new AgentKey(456, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Response")] },
            (AiResponse?)null);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { response }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert - WebUI should receive the response
        webUiUpdates.Count.ShouldBe(1);
        // ServiceBus should NOT receive it (source doesn't match and it's not WebUI)
        serviceBusUpdates.Count.ShouldBe(0);
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_UnknownChatIdBroadcastsToAll()
    {
        // Arrange
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();
        var serviceBusUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus, serviceBusUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, serviceBusClient.Object]);

        // Don't read any prompts - ChatId 789 will be unknown

        // Create response for unknown ChatId
        var response = (
            new AgentKey(789, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Response")] },
            (AiResponse?)null);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { response }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert - Both should receive it as fail-safe (unknown source defaults to broadcast)
        webUiUpdates.Count.ShouldBe(1);
        serviceBusUpdates.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_TelegramClientOnlyReceivesTelegramResponses()
    {
        // Arrange
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();
        var telegramUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var telegramClient = CreateMockClient(MessageSource.Telegram, telegramUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, telegramClient.Object]);

        // Simulate reading a prompt from Telegram
        var telegramPrompt = new ChatPrompt
        {
            Prompt = "Hello from Telegram",
            ChatId = 100,
            ThreadId = 1,
            MessageId = 1,
            Sender = "user",
            Source = MessageSource.Telegram
        };

        telegramClient.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { telegramPrompt }.ToAsyncEnumerable());
        webUiClient.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<ChatPrompt>());

        // Read prompts to establish source tracking
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var _ in composite.ReadPrompts(100, cts.Token).Take(1)) { }

        // Create response for the Telegram prompt
        var response = (
            new AgentKey(100, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Response")] },
            (AiResponse?)null);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { response }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert
        webUiUpdates.Count.ShouldBe(1);  // WebUI receives all
        telegramUpdates.Count.ShouldBe(1);  // Telegram receives its own
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_SameChatIdDifferentSources_UsesLatestSource()
    {
        // Arrange
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();
        var serviceBusUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus, serviceBusUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, serviceBusClient.Object]);

        // First prompt from ServiceBus
        var serviceBusPrompt = new ChatPrompt
        {
            Prompt = "First from ServiceBus",
            ChatId = 200,
            ThreadId = 1,
            MessageId = 1,
            Sender = "user",
            Source = MessageSource.ServiceBus
        };

        // Second prompt from WebUI with SAME ChatId
        var webUiPrompt = new ChatPrompt
        {
            Prompt = "Second from WebUI",
            ChatId = 200,
            ThreadId = 1,
            MessageId = 2,
            Sender = "user",
            Source = MessageSource.WebUi
        };

        serviceBusClient.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { serviceBusPrompt }.ToAsyncEnumerable());
        webUiClient.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { webUiPrompt }.ToAsyncEnumerable());

        // Read prompts (ServiceBus first, then WebUI - WebUI overwrites source)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var _ in composite.ReadPrompts(100, cts.Token).Take(2)) { }

        // Create response for ChatId 200
        var response = (
            new AgentKey(200, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Response")] },
            (AiResponse?)null);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { response }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert - Latest source was WebUI, so ServiceBus should NOT receive
        webUiUpdates.Count.ShouldBe(1);
        serviceBusUpdates.Count.ShouldBe(0);
    }

    private static Mock<IChatMessengerClient> CreateMockClient(
        MessageSource source,
        List<(AgentKey, AgentResponseUpdate, AiResponse?)> receivedUpdates)
    {
        var mock = new Mock<IChatMessengerClient>();
        mock.Setup(c => c.Source).Returns(source);
        mock.Setup(c => c.SupportsScheduledNotifications).Returns(false);
        mock.Setup(c => c.ProcessResponseStreamAsync(
                It.IsAny<IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
                CancellationToken ct) =>
            {
                await foreach (var update in updates.WithCancellation(ct))
                {
                    receivedUpdates.Add(update);
                }
            });
        return mock;
    }
}

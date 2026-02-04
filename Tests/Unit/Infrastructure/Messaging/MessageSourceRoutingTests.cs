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
    private readonly IMessageSourceRouter _router = new MessageSourceRouter();

    [Fact]
    public async Task ProcessResponseStreamAsync_WebUiClientReceivesAllResponses()
    {
        // Arrange
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();
        var serviceBusUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus, serviceBusUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, serviceBusClient.Object], _router);

        // Create response for ServiceBus with source in tuple
        var response = (
            new AgentKey(123, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Response")] },
            (AiResponse?)null,
            MessageSource.ServiceBus);

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
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();
        var serviceBusUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus, serviceBusUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, serviceBusClient.Object], _router);

        // Create response for WebUI with source in tuple
        var response = (
            new AgentKey(456, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Response")] },
            (AiResponse?)null,
            MessageSource.WebUi);

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
    public async Task ProcessResponseStreamAsync_CliSourceBroadcastsToWebUiOnly()
    {
        // Arrange
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();
        var serviceBusUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus, serviceBusUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, serviceBusClient.Object], _router);

        // Create response with Cli source (only WebUI should receive it as universal viewer)
        var response = (
            new AgentKey(789, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Response")] },
            (AiResponse?)null,
            MessageSource.Cli);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { response }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert - Only WebUI receives it (universal viewer), ServiceBus doesn't match source
        webUiUpdates.Count.ShouldBe(1);
        serviceBusUpdates.Count.ShouldBe(0);
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_TelegramClientOnlyReceivesTelegramResponses()
    {
        // Arrange
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();
        var telegramUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var telegramClient = CreateMockClient(MessageSource.Telegram, telegramUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, telegramClient.Object], _router);

        // Create response for Telegram with source in tuple
        var response = (
            new AgentKey(100, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Response")] },
            (AiResponse?)null,
            MessageSource.Telegram);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { response }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert
        webUiUpdates.Count.ShouldBe(1); // WebUI receives all
        telegramUpdates.Count.ShouldBe(1); // Telegram receives its own
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_SameChatIdDifferentSources_RoutesCorrectly()
    {
        // Arrange
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();
        var serviceBusUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus, serviceBusUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, serviceBusClient.Object], _router);

        // Two responses with SAME ChatId but different sources - should route correctly based on tuple source
        var serviceBusResponse = (
            new AgentKey(200, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("ServiceBus Response")] },
            (AiResponse?)null,
            MessageSource.ServiceBus);

        var webUiResponse = (
            new AgentKey(200, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("WebUI Response")] },
            (AiResponse?)null,
            MessageSource.WebUi);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { serviceBusResponse, webUiResponse }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert - Both should route correctly based on tuple source (not dictionary tracking)
        // WebUI receives both (universal viewer)
        webUiUpdates.Count.ShouldBe(2);
        // ServiceBus receives only its own response
        serviceBusUpdates.Count.ShouldBe(1);
    }

    private static Mock<IChatMessengerClient> CreateMockClient(
        MessageSource source,
        List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> receivedUpdates)
    {
        var mock = new Mock<IChatMessengerClient>();
        mock.Setup(c => c.Source).Returns(source);
        mock.Setup(c => c.SupportsScheduledNotifications).Returns(false);
        mock.Setup(c => c.ProcessResponseStreamAsync(
                It.IsAny<IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
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
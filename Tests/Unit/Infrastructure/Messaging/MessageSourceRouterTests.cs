using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients.Messaging;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Messaging;

public class MessageSourceRouterTests
{
    private readonly MessageSourceRouter _router = new();

    [Fact]
    public void GetClientsForSource_WebUiSource_ReturnsOnlyWebUiClients()
    {
        // Arrange
        var webUiClient = CreateMockClient(MessageSource.WebUi);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus);
        var clients = new[] { webUiClient, serviceBusClient };

        // Act
        var result = _router.GetClientsForSource(clients, MessageSource.WebUi).ToList();

        // Assert
        result.Count.ShouldBe(1);
        result.ShouldContain(webUiClient);
    }

    [Fact]
    public void GetClientsForSource_ServiceBusSource_ReturnsWebUiAndServiceBusClients()
    {
        // Arrange
        var webUiClient = CreateMockClient(MessageSource.WebUi);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus);
        var telegramClient = CreateMockClient(MessageSource.Telegram);
        var clients = new[] { webUiClient, serviceBusClient, telegramClient };

        // Act
        var result = _router.GetClientsForSource(clients, MessageSource.ServiceBus).ToList();

        // Assert
        result.Count.ShouldBe(2);
        result.ShouldContain(webUiClient);
        result.ShouldContain(serviceBusClient);
        result.ShouldNotContain(telegramClient);
    }

    [Fact]
    public void GetClientsForSource_TelegramSource_ReturnsWebUiAndTelegramClients()
    {
        // Arrange
        var webUiClient = CreateMockClient(MessageSource.WebUi);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus);
        var telegramClient = CreateMockClient(MessageSource.Telegram);
        var clients = new[] { webUiClient, serviceBusClient, telegramClient };

        // Act
        var result = _router.GetClientsForSource(clients, MessageSource.Telegram).ToList();

        // Assert
        result.Count.ShouldBe(2);
        result.ShouldContain(webUiClient);
        result.ShouldContain(telegramClient);
        result.ShouldNotContain(serviceBusClient);
    }

    [Fact]
    public void GetClientsForSource_NoMatchingClients_ReturnsOnlyWebUi()
    {
        // Arrange
        var webUiClient = CreateMockClient(MessageSource.WebUi);
        var telegramClient = CreateMockClient(MessageSource.Telegram);
        var clients = new[] { webUiClient, telegramClient };

        // Act
        var result = _router.GetClientsForSource(clients, MessageSource.ServiceBus).ToList();

        // Assert
        result.Count.ShouldBe(1);
        result.ShouldContain(webUiClient);
    }

    private static IChatMessengerClient CreateMockClient(MessageSource source)
    {
        var mock = new Mock<IChatMessengerClient>();
        mock.Setup(c => c.Source).Returns(source);
        return mock.Object;
    }
}

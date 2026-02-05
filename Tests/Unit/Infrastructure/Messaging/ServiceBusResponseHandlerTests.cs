using Domain.Agents;
using Domain.DTOs;
using Infrastructure.Clients.Messaging.ServiceBus;
using Microsoft.Agents.AI;
using Moq;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusResponseHandlerTests
{
    [Fact]
    public async Task ProcessAsync_CompletedResponse_WritesToServiceBus()
    {
        // Arrange
        var (handler, receiverMock, writerMock) = CreateHandler();
        var chatId = 123L;

        receiverMock.Setup(r => r.TryGetCorrelationId(chatId, out It.Ref<string>.IsAny))
            .Returns((long _, out string s) =>
            {
                s = "correlation-123";
                return true;
            });

        var updates = CreateUpdates(chatId, "agent-1", new AiResponse { Content = "Hello World" });

        // Act
        await handler.ProcessAsync(updates, CancellationToken.None);

        // Assert
        writerMock.Verify(w => w.WriteResponseAsync(
            "correlation-123",
            "agent-1",
            "Hello World",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_UnknownChatId_SkipsUpdate()
    {
        // Arrange
        var (handler, receiverMock, writerMock) = CreateHandler();

        receiverMock.Setup(r => r.TryGetCorrelationId(It.IsAny<long>(), out It.Ref<string>.IsAny))
            .Returns(false);

        var updates = CreateUpdates(999, "agent-1", new AiResponse { Content = "Hello" });

        // Act
        await handler.ProcessAsync(updates, CancellationToken.None);

        // Assert
        writerMock.Verify(w => w.WriteResponseAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_NullAiResponse_DoesNotWrite()
    {
        // Arrange
        var (handler, receiverMock, writerMock) = CreateHandler();
        var chatId = 123L;

        receiverMock.Setup(r => r.TryGetCorrelationId(chatId, out It.Ref<string>.IsAny))
            .Returns((long _, out string s) =>
            {
                s = "correlation-123";
                return true;
            });

        var updates = CreateUpdates(chatId, "agent-1", null);

        // Act
        await handler.ProcessAsync(updates, CancellationToken.None);

        // Assert
        writerMock.Verify(w => w.WriteResponseAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_EmptyContent_DoesNotWrite()
    {
        // Arrange
        var (handler, receiverMock, writerMock) = CreateHandler();
        var chatId = 123L;

        receiverMock.Setup(r => r.TryGetCorrelationId(chatId, out It.Ref<string>.IsAny))
            .Returns((long _, out string s) =>
            {
                s = "correlation-123";
                return true;
            });

        var updates = CreateUpdates(chatId, "agent-1", new AiResponse { Content = "" });

        // Act
        await handler.ProcessAsync(updates, CancellationToken.None);

        // Assert
        writerMock.Verify(w => w.WriteResponseAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static (ServiceBusResponseHandler handler, Mock<ServiceBusPromptReceiver> receiverMock,
        Mock<ServiceBusResponseWriter> writerMock) CreateHandler()
    {
        var receiverMock = new Mock<ServiceBusPromptReceiver>(null!, null!, null!);
        var writerMock = new Mock<ServiceBusResponseWriter>(null!, null!);

        var handler = new ServiceBusResponseHandler(
            receiverMock.Object,
            writerMock.Object);

        return (handler, receiverMock, writerMock);
    }

    private static async IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> CreateUpdates(
        long chatId,
        string? agentId,
        AiResponse? response)
    {
        await Task.CompletedTask;
        yield return (new AgentKey(chatId, 1, agentId), new AgentResponseUpdate(), response, MessageSource.ServiceBus);
    }
}
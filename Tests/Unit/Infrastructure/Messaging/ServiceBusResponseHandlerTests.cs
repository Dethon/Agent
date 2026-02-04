using Domain.Agents;
using Domain.DTOs;
using Infrastructure.Clients.Messaging.ServiceBus;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusResponseHandlerTests
{
    [Fact]
    public async Task ProcessAsync_TextContent_AccumulatesText()
    {
        // Arrange
        var (handler, receiverMock, writerMock) = CreateHandler();
        var chatId = 123L;

        receiverMock.Setup(r => r.TryGetSourceId(chatId, out It.Ref<string>.IsAny))
            .Returns((long _, out string s) =>
            {
                s = "source-123";
                return true;
            });

        var updates = CreateUpdates(chatId, [
            new AgentResponseUpdate { Contents = [new TextContent("Hello ")] },
            new AgentResponseUpdate { Contents = [new TextContent("World")] },
            new AgentResponseUpdate { Contents = [new StreamCompleteContent()] }
        ]);

        // Act
        await handler.ProcessAsync(updates, CancellationToken.None);

        // Assert
        writerMock.Verify(w => w.WriteResponseAsync(
            "source-123",
            "agent-1",
            "Hello World",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_UnknownChatId_SkipsUpdate()
    {
        // Arrange
        var (handler, receiverMock, writerMock) = CreateHandler();

        receiverMock.Setup(r => r.TryGetSourceId(It.IsAny<long>(), out It.Ref<string>.IsAny))
            .Returns(false);

        var updates = CreateUpdates(999, [
            new AgentResponseUpdate { Contents = [new TextContent("Hello")] }
        ]);

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
    public async Task ProcessAsync_EmptyAccumulator_DoesNotWriteOnComplete()
    {
        // Arrange
        var (handler, receiverMock, writerMock) = CreateHandler();
        var chatId = 123L;

        receiverMock.Setup(r => r.TryGetSourceId(chatId, out It.Ref<string>.IsAny))
            .Returns((long _, out string s) =>
            {
                s = "source-123";
                return true;
            });

        var updates = CreateUpdates(chatId, [
            new AgentResponseUpdate { Contents = [new StreamCompleteContent()] }
        ]);

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
        var receiverMock = new Mock<ServiceBusPromptReceiver>(null!, null!);
        var writerMock = new Mock<ServiceBusResponseWriter>(null!, null!);

        var handler = new ServiceBusResponseHandler(
            receiverMock.Object,
            writerMock.Object,
            "default-agent");

        return (handler, receiverMock, writerMock);
    }

    private static async IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> CreateUpdates(
        long chatId,
        AgentResponseUpdate[] updates)
    {
        foreach (var update in updates)
        {
            yield return (new AgentKey(chatId, 1, "agent-1"), update, null, MessageSource.ServiceBus);
        }

        await Task.CompletedTask;
    }
}
using Azure.Messaging.ServiceBus;
using Infrastructure.Clients.Messaging;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusResponseWriterTests
{
    [Fact]
    public async Task WriteResponseAsync_SuccessfulSend_LogsDebug()
    {
        // Arrange
        var senderMock = new Mock<ServiceBusSender>();
        var loggerMock = new Mock<ILogger<ServiceBusResponseWriter>>();

        senderMock.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var writer = new ServiceBusResponseWriter(senderMock.Object, loggerMock.Object);

        // Act
        await writer.WriteResponseAsync("source-123", "agent-1", "Hello response");

        // Assert
        senderMock.Verify(s => s.SendMessageAsync(
            It.Is<ServiceBusMessage>(m => m.ContentType == "application/json"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WriteResponseAsync_TransientFailureThenSuccess_Retries()
    {
        // Arrange
        var senderMock = new Mock<ServiceBusSender>();
        var loggerMock = new Mock<ILogger<ServiceBusResponseWriter>>();
        var callCount = 0;

        senderMock.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount < 2)
                {
                    throw new ServiceBusException("Transient", ServiceBusFailureReason.ServiceBusy);
                }
                return Task.CompletedTask;
            });

        var writer = new ServiceBusResponseWriter(senderMock.Object, loggerMock.Object);

        // Act
        await writer.WriteResponseAsync("source-123", "agent-1", "Hello response");

        // Assert
        callCount.ShouldBe(2); // Initial + 1 retry
    }

    [Fact]
    public async Task WriteResponseAsync_AllRetriesExhausted_LogsErrorAndDoesNotThrow()
    {
        // Arrange
        var senderMock = new Mock<ServiceBusSender>();
        var loggerMock = new Mock<ILogger<ServiceBusResponseWriter>>();

        senderMock.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceBusException("Transient", ServiceBusFailureReason.ServiceBusy));

        var writer = new ServiceBusResponseWriter(senderMock.Object, loggerMock.Object);

        // Act - should not throw
        await Should.NotThrowAsync(async () =>
            await writer.WriteResponseAsync("source-123", "agent-1", "Hello response"));

        // Assert - called 4 times (initial + 3 retries)
        senderMock.Verify(s => s.SendMessageAsync(
            It.IsAny<ServiceBusMessage>(),
            It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    [Fact]
    public async Task WriteResponseAsync_NonTransientFailure_DoesNotRetry()
    {
        // Arrange
        var senderMock = new Mock<ServiceBusSender>();
        var loggerMock = new Mock<ILogger<ServiceBusResponseWriter>>();

        senderMock.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Non-transient error"));

        var writer = new ServiceBusResponseWriter(senderMock.Object, loggerMock.Object);

        // Act - should not throw (error is caught and logged)
        await Should.NotThrowAsync(async () =>
            await writer.WriteResponseAsync("source-123", "agent-1", "Hello response"));

        // Assert - called only once (no retry for non-transient)
        senderMock.Verify(s => s.SendMessageAsync(
            It.IsAny<ServiceBusMessage>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}

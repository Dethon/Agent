using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Domain.DTOs;
using McpChannelServiceBus.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelServiceBus;

public class ResponseSenderTests
{
    private readonly Mock<ServiceBusSender> _sender = new();
    private readonly ResponseSender _sut;
    private ServiceBusMessage? _capturedMessage;

    public ResponseSenderTests()
    {
        _sender
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((msg, _) => _capturedMessage = msg)
            .Returns(Task.CompletedTask);

        _sut = new ResponseSender(_sender.Object, new Mock<ILogger<ResponseSender>>().Object);
    }

    [Fact]
    public async Task SendResponseAsync_SendsWellFormedMessage()
    {
        using var cts = new CancellationTokenSource();

        await _sut.SendResponseAsync("corr-123", "Hello response", cts.Token);

        // ReSharper disable once AccessToDisposedClosure
        _sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), cts.Token), Times.Once);

        _capturedMessage.ShouldNotBeNull();
        _capturedMessage.CorrelationId.ShouldBe("corr-123");
        _capturedMessage.ContentType.ShouldBe("application/json");

        var body = _capturedMessage.Body.ToString();
        var deserialized = JsonSerializer.Deserialize<ServiceBusResponseMessage>(body);

        deserialized.ShouldNotBeNull();
        deserialized.CorrelationId.ShouldBe("corr-123");
        deserialized.Response.ShouldBe("Hello response");
        deserialized.AgentId.ShouldBe("default");
        deserialized.CompletedAt.ShouldNotBe(default);
    }
}
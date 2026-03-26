using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Domain.DTOs;
using McpChannelServiceBus.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelServiceBus;

public class ServiceBusProcessorServiceTests : IDisposable
{
    private readonly Mock<ServiceBusProcessor> _processor = new();
    private readonly ChannelNotificationEmitter _emitter;
    private readonly ServiceBusProcessorService _sut;
    private readonly CancellationTokenSource _cts = new();

    public ServiceBusProcessorServiceTests()
    {
        _emitter = new ChannelNotificationEmitter(new Mock<ILogger<ChannelNotificationEmitter>>().Object);

        _processor
            .Setup(p => p.StartProcessingAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _processor
            .Setup(p => p.StopProcessingAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new ServiceBusProcessorService(
            _processor.Object,
            _emitter,
            new Mock<ILogger<ServiceBusProcessorService>>().Object);
    }

    [Fact]
    public async Task ExecuteAsync_StartsProcessor()
    {
        await StartAndStopService();

        _processor.Verify(p => p.StartProcessingAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StopAsync_StopsProcessor()
    {
        await StartAndStopService();

        _processor.Verify(p => p.StopProcessingAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMessage_ValidPrompt_WithActiveSessions_CompletesMessage()
    {
        _emitter.RegisterSession("sess-1", null!);

        var receiver = new Mock<ServiceBusReceiver>();
        receiver
            .Setup(r => r.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var message = CreateReceivedMessage(new ServiceBusPromptMessage
        {
            CorrelationId = "corr-1",
            AgentId = "jack",
            Prompt = "Hello",
            Sender = "user1"
        });

        var args = new ProcessMessageEventArgs(message, receiver.Object, CancellationToken.None);
        await _sut.ProcessMessageAsync(args);

        receiver.Verify(r => r.CompleteMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ProcessMessage_MissingOrEmptyPrompt_DeadLettersMessage(string? prompt)
    {
        _emitter.RegisterSession("sess-1", null!);

        var receiver = new Mock<ServiceBusReceiver>();
        receiver
            .Setup(r => r.DeadLetterMessageAsync(
                It.IsAny<ServiceBusReceivedMessage>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var message = CreateReceivedMessage(new ServiceBusPromptMessage
        {
            CorrelationId = "corr-1",
            Prompt = prompt
        });

        var args = new ProcessMessageEventArgs(message, receiver.Object, CancellationToken.None);
        await _sut.ProcessMessageAsync(args);

        receiver.Verify(r => r.DeadLetterMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(),
            "InvalidMessage",
            "Missing required fields",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMessage_NoActiveSessions_AbandonsMessage()
    {
        // Don't register any sessions

        var receiver = new Mock<ServiceBusReceiver>();
        receiver
            .Setup(r => r.AbandonMessageAsync(
                It.IsAny<ServiceBusReceivedMessage>(),
                It.IsAny<IDictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var message = CreateReceivedMessage(new ServiceBusPromptMessage
        {
            CorrelationId = "corr-1",
            Prompt = "Hello"
        });

        var args = new ProcessMessageEventArgs(message, receiver.Object, CancellationToken.None);
        await _sut.ProcessMessageAsync(args);

        receiver.Verify(r => r.AbandonMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(),
            It.IsAny<IDictionary<string, object>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMessage_NullCorrelationId_GeneratesFallback()
    {
        _emitter.RegisterSession("sess-1", null!);

        var receiver = new Mock<ServiceBusReceiver>();
        receiver
            .Setup(r => r.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var message = CreateReceivedMessage(new ServiceBusPromptMessage
        {
            CorrelationId = null,
            Prompt = "Hello"
        });

        var args = new ProcessMessageEventArgs(message, receiver.Object, CancellationToken.None);

        await Should.NotThrowAsync(() => _sut.ProcessMessageAsync(args));

        receiver.Verify(r => r.CompleteMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMessage_MissingSender_DefaultsToServiceBus()
    {
        _emitter.RegisterSession("sess-1", null!);

        var receiver = new Mock<ServiceBusReceiver>();
        receiver
            .Setup(r => r.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var message = CreateReceivedMessage(new ServiceBusPromptMessage
        {
            CorrelationId = "corr-1",
            Prompt = "Hello",
            Sender = null
        });

        var args = new ProcessMessageEventArgs(message, receiver.Object, CancellationToken.None);

        // Should complete without error using "service-bus" as fallback sender
        await Should.NotThrowAsync(() => _sut.ProcessMessageAsync(args));

        receiver.Verify(r => r.CompleteMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private async Task StartAndStopService()
    {
        _cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        await _sut.StartAsync(_cts.Token);
        await Task.Delay(50, CancellationToken.None);
        await _sut.StopAsync(CancellationToken.None);
    }

    private static ServiceBusReceivedMessage CreateReceivedMessage(ServiceBusPromptMessage prompt)
    {
        var json = JsonSerializer.Serialize(prompt);
        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(json),
            messageId: Guid.NewGuid().ToString());
    }

    public void Dispose() => _cts.Dispose();
}
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using DotNet.Testcontainers.Builders;
using Infrastructure.Clients.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Testcontainers.ServiceBus;

namespace Tests.Integration.Fixtures;

public class ServiceBusFixture : IAsyncLifetime
{
    private ServiceBusContainer _serviceBusContainer = null!;
    private RedisFixture _redisFixture = null!;
    private ServiceBusClient _serviceBusClient = null!;
    private ServiceBusSender _promptSender = null!;
    private ServiceBusSender _responseSender = null!;
    private ServiceBusReceiver _responseReceiver = null!;

    public const string PromptQueueName = "agent-prompts";
    public const string ResponseQueueName = "agent-responses";
    public const string DefaultAgentId = "test-agent";

    public string ConnectionString { get; private set; } = null!;
    public IConnectionMultiplexer RedisConnection => _redisFixture.Connection;

    public async Task InitializeAsync()
    {
        _redisFixture = new RedisFixture();
        await _redisFixture.InitializeAsync();

        var configPath = Path.Combine(AppContext.BaseDirectory, "Integration", "Fixtures", "ServiceBusConfig.json");

        _serviceBusContainer = new ServiceBusBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
            .WithAcceptLicenseAgreement(true)
            .WithConfig(configPath)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Emulator Service is Successfully Up!")
                .UntilHttpRequestIsSucceeded(
                    request => request.ForPort(5300).ForPath("/health"),
                    waitStrategy => waitStrategy.WithTimeout(TimeSpan.FromMinutes(3))))
            .Build();

        await _serviceBusContainer.StartAsync();

        ConnectionString = _serviceBusContainer.GetConnectionString();
        _serviceBusClient = new ServiceBusClient(ConnectionString);

        _promptSender = _serviceBusClient.CreateSender(PromptQueueName);
        _responseSender = _serviceBusClient.CreateSender(ResponseQueueName);
        _responseReceiver = _serviceBusClient.CreateReceiver(ResponseQueueName);
    }

    public async Task DisposeAsync()
    {
        await _responseReceiver.DisposeAsync();
        await _responseSender.DisposeAsync();
        await _promptSender.DisposeAsync();
        await _serviceBusClient.DisposeAsync();
        await _serviceBusContainer.DisposeAsync();
        await _redisFixture.DisposeAsync();
    }

    public async Task SendPromptAsync(
        string prompt,
        string sender,
        string? sourceId = null,
        string? agentId = null)
    {
        var messageBody = new { prompt, sender };
        var json = JsonSerializer.Serialize(messageBody);
        var message = new ServiceBusMessage(BinaryData.FromString(json))
        {
            ContentType = "application/json"
        };

        if (sourceId is not null)
        {
            message.ApplicationProperties["sourceId"] = sourceId;
        }

        if (agentId is not null)
        {
            message.ApplicationProperties["agentId"] = agentId;
        }

        await _promptSender.SendMessageAsync(message);
    }

    public async Task SendRawMessageAsync(string rawJson)
    {
        var message = new ServiceBusMessage(BinaryData.FromString(rawJson))
        {
            ContentType = "application/json"
        };
        await _promptSender.SendMessageAsync(message);
    }

    public async Task<ServiceBusReceivedMessage?> ReceiveResponseAsync(TimeSpan timeout)
    {
        return await _responseReceiver.ReceiveMessageAsync(timeout);
    }

    public async Task CompleteResponseAsync(ServiceBusReceivedMessage message)
    {
        await _responseReceiver.CompleteMessageAsync(message);
    }

    public async Task<IReadOnlyList<ServiceBusReceivedMessage>> GetDeadLetterMessagesAsync(int maxMessages = 10)
    {
        await using var dlqReceiver = _serviceBusClient.CreateReceiver(
            PromptQueueName,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

        var messages = await dlqReceiver.ReceiveMessagesAsync(maxMessages, TimeSpan.FromSeconds(5));
        return messages;
    }

    public (ServiceBusChatMessengerClient Client, ServiceBusProcessorHost Host) CreateClientAndHost()
    {
        var threadStateStoreMock = new Mock<IThreadStateStore>();
        threadStateStoreMock
            .Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Returns(Task.CompletedTask);

        var sourceMapper = new ServiceBusConversationMapper(
            RedisConnection,
            threadStateStoreMock.Object,
            NullLogger<ServiceBusConversationMapper>.Instance);

        var responseWriter = new ServiceBusResponseWriter(
            _responseSender,
            NullLogger<ServiceBusResponseWriter>.Instance);

        var promptReceiver = new ServiceBusPromptReceiver(
            sourceMapper,
            NullLogger<ServiceBusPromptReceiver>.Instance);

        var responseHandler = new ServiceBusResponseHandler(
            promptReceiver,
            responseWriter,
            DefaultAgentId);

        var client = new ServiceBusChatMessengerClient(
            promptReceiver,
            responseHandler,
            DefaultAgentId);

        var processor = _serviceBusClient.CreateProcessor(PromptQueueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 1
        });

        var messageParser = new ServiceBusMessageParser([DefaultAgentId]);

        var host = new ServiceBusProcessorHost(
            processor,
            messageParser,
            promptReceiver,
            NullLogger<ServiceBusProcessorHost>.Instance);

        return (client, host);
    }
}
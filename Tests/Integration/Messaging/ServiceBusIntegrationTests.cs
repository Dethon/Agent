using System.Text.Json;
using Domain.Agents;
using Domain.DTOs;
using Infrastructure.Clients.Messaging.ServiceBus;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Messaging;

public class ServiceBusIntegrationTests(ServiceBusFixture fixture)
    : IClassFixture<ServiceBusFixture>, IAsyncLifetime
{
    private ServiceBusChatMessengerClient _messengerClient = null!;
    private ServiceBusProcessorHost _processorHost = null!;
    private CancellationTokenSource _cts = null!;

    public async Task InitializeAsync()
    {
        (_messengerClient, _processorHost) = fixture.CreateClientAndHost();
        _cts = new CancellationTokenSource();

        await _processorHost.StartAsync(_cts.Token);
    }

    public async Task DisposeAsync()
    {
        await _cts.CancelAsync();
        await _processorHost.StopAsync(CancellationToken.None);
        _cts.Dispose();
    }

    [Fact]
    public async Task SendPrompt_ValidMessage_ProcessedAndResponseWritten()
    {
        // Arrange
        var correlationId = $"test-{Guid.NewGuid():N}";
        const string prompt = "Hello, agent!";
        const string sender = "test-user";
        const string expectedResponse = "Hello back!";

        // Act - Send prompt
        await fixture.SendPromptAsync(prompt, sender, correlationId);

        // Wait for prompt to be enqueued
        await Task.Delay(500);

        // Read and verify the prompt was enqueued
        var prompts = new List<ChatPrompt>();
        var readTask = Task.Run(async () =>
        {
            await foreach (var p in _messengerClient.ReadPrompts(0, _cts.Token))
            {
                prompts.Add(p);
                break;
            }
        });

        await Task.WhenAny(readTask, Task.Delay(5000));
        prompts.ShouldHaveSingleItem();
        prompts[0].Prompt.ShouldBe(prompt);
        prompts[0].Sender.ShouldBe(sender);

        // Simulate agent response
        var agentKey = new AgentKey(prompts[0].ChatId, prompts[0].ThreadId ?? 0, ServiceBusFixture.DefaultAgentId);
        var responseStream = CreateResponseStream(agentKey, expectedResponse);
        await _messengerClient.ProcessResponseStreamAsync(responseStream, _cts.Token);

        // Assert - Verify response on response queue
        var response = await fixture.ReceiveResponseAsync(TimeSpan.FromSeconds(10));
        response.ShouldNotBeNull();

        var responseBody = JsonSerializer.Deserialize<JsonElement>(response.Body.ToString());
        responseBody.GetProperty("correlationId").GetString().ShouldBe(correlationId);
        responseBody.GetProperty("response").GetString().ShouldBe(expectedResponse);
        responseBody.GetProperty("agentId").GetString().ShouldBe(ServiceBusFixture.DefaultAgentId);

        await fixture.CompleteResponseAsync(response);
    }

    [Fact]
    public async Task SendPrompt_MissingCorrelationId_DeadLettered()
    {
        // Arrange - Send JSON without correlationId field
        const string missingCorrelationIdJson = """{"agentId": "test-agent", "prompt": "test prompt", "sender": "test-user"}""";

        // Act
        await fixture.SendRawMessageAsync(missingCorrelationIdJson);

        // Wait for processing
        await Task.Delay(1000);

        // Assert - Message should be in dead-letter queue with MissingField reason
        var deadLetterMessages = await fixture.GetDeadLetterMessagesAsync();
        deadLetterMessages.ShouldNotBeEmpty();

        var dlMessage = deadLetterMessages.First();
        dlMessage.DeadLetterReason.ShouldBe("MissingField");
    }

    [Fact]
    public async Task SendPrompt_MalformedJson_DeadLettered()
    {
        // Arrange - Send invalid JSON
        const string malformedJson = "{ this is not valid json }";

        // Act
        await fixture.SendRawMessageAsync(malformedJson);

        // Wait for processing
        await Task.Delay(1000);

        // Assert - Message should be in dead-letter queue
        var deadLetterMessages = await fixture.GetDeadLetterMessagesAsync();
        deadLetterMessages.ShouldNotBeEmpty();

        var dlMessage = deadLetterMessages.First();
        dlMessage.DeadLetterReason.ShouldBe("DeserializationError");
    }

    [Fact]
    public async Task SendPrompt_MissingPromptField_DeadLettered()
    {
        // Arrange - Send JSON without required 'prompt' field
        const string missingPromptJson = """{"correlationId": "test-123", "agentId": "test-agent", "sender": "test-user"}""";

        // Act
        await fixture.SendRawMessageAsync(missingPromptJson);

        // Wait for processing
        await Task.Delay(1000);

        // Assert - Message should be in dead-letter queue with MissingField reason
        var deadLetterMessages = await fixture.GetDeadLetterMessagesAsync();
        deadLetterMessages.ShouldNotBeEmpty();

        var dlMessage = deadLetterMessages.First();
        dlMessage.DeadLetterReason.ShouldBe("MissingField");
    }

    [Fact]
    public async Task SendPrompt_SameCorrelationId_SameChatIdThreadId()
    {
        // Arrange
        var correlationId = $"test-{Guid.NewGuid():N}";
        const string sender = "test-user";

        // Act - Send two prompts with the same correlationId
        await fixture.SendPromptAsync("First message", sender, correlationId);
        await Task.Delay(300);
        await fixture.SendPromptAsync("Second message", sender, correlationId);

        // Collect both prompts
        var prompts = new List<ChatPrompt>();
        var readTask = Task.Run(async () =>
        {
            await foreach (var p in _messengerClient.ReadPrompts(0, _cts.Token))
            {
                prompts.Add(p);
                if (prompts.Count >= 2)
                {
                    break;
                }
            }
        });

        await Task.WhenAny(readTask, Task.Delay(10000));

        // Assert - Both prompts have the same chatId and threadId
        prompts.Count.ShouldBe(2);
        prompts[0].ChatId.ShouldBe(prompts[1].ChatId);
        prompts[0].ThreadId.ShouldBe(prompts[1].ThreadId);
    }

    [Fact]
    public async Task SendPrompt_DifferentCorrelationIds_DifferentChatIds()
    {
        // Arrange
        var correlationId1 = $"test-{Guid.NewGuid():N}";
        var correlationId2 = $"test-{Guid.NewGuid():N}";
        const string sender = "test-user";

        // Act - Send two prompts with different correlationIds
        await fixture.SendPromptAsync("First correlation message", sender, correlationId1);
        await Task.Delay(300);
        await fixture.SendPromptAsync("Second correlation message", sender, correlationId2);

        // Collect both prompts
        var prompts = new List<ChatPrompt>();
        var readTask = Task.Run(async () =>
        {
            await foreach (var p in _messengerClient.ReadPrompts(0, _cts.Token))
            {
                prompts.Add(p);
                if (prompts.Count >= 2)
                {
                    break;
                }
            }
        });

        await Task.WhenAny(readTask, Task.Delay(10000));

        // Assert - Prompts have different chatIds
        prompts.Count.ShouldBe(2);
        prompts[0].ChatId.ShouldNotBe(prompts[1].ChatId);
    }

    [Fact]
    public async Task SendPrompt_InvalidAgentId_DeadLettered()
    {
        // Arrange - Send JSON with an agentId that is not configured
        const string invalidAgentIdJson = """{"correlationId": "test-123", "agentId": "unknown-agent", "prompt": "test prompt", "sender": "test-user"}""";

        // Act
        await fixture.SendRawMessageAsync(invalidAgentIdJson);

        // Wait for processing
        await Task.Delay(1000);

        // Assert - Message should be in dead-letter queue with InvalidAgentId reason
        var deadLetterMessages = await fixture.GetDeadLetterMessagesAsync();
        deadLetterMessages.ShouldNotBeEmpty();

        var dlMessage = deadLetterMessages.First();
        dlMessage.DeadLetterReason.ShouldBe("InvalidAgentId");
    }

    private static async IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>
        CreateResponseStream(
            AgentKey key,
            string responseText)
    {
        await Task.CompletedTask;

        yield return (key, new AgentResponseUpdate
        {
            MessageId = "msg-1",
            Contents = [new TextContent(responseText)]
        }, null, MessageSource.ServiceBus);

        yield return (key, new AgentResponseUpdate
        {
            MessageId = "msg-1",
            Contents = [new StreamCompleteContent()]
        }, new AiResponse { Content = responseText }, MessageSource.ServiceBus);
    }
}
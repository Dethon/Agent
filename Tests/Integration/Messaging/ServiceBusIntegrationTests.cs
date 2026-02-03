using System.Text.Json;
using global::Domain.Agents;
using global::Domain.DTOs;
using Infrastructure.Clients.Messaging;
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
        _messengerClient = fixture.CreateMessengerClient();
        _processorHost = fixture.CreateProcessorHost(_messengerClient);
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
        var sourceId = $"test-{Guid.NewGuid():N}";
        const string prompt = "Hello, agent!";
        const string sender = "test-user";
        const string expectedResponse = "Hello back!";

        // Act - Send prompt
        await fixture.SendPromptAsync(prompt, sender, sourceId);

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
        responseBody.GetProperty("SourceId").GetString().ShouldBe(sourceId);
        responseBody.GetProperty("Response").GetString().ShouldBe(expectedResponse);
        responseBody.GetProperty("AgentId").GetString().ShouldBe(ServiceBusFixture.DefaultAgentId);

        await fixture.CompleteResponseAsync(response);
    }

    [Fact]
    public async Task SendPrompt_MissingSourceId_GeneratesUuidAndProcesses()
    {
        // Arrange
        const string prompt = "No source ID message";
        const string sender = "test-user";

        // Act - Send prompt without sourceId
        await fixture.SendPromptAsync(prompt, sender, sourceId: null);

        // Wait for prompt to be enqueued
        await Task.Delay(500);

        // Read the prompt
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

        // Assert - Prompt was processed (sourceId was auto-generated)
        prompts.ShouldHaveSingleItem();
        prompts[0].Prompt.ShouldBe(prompt);
        prompts[0].ChatId.ShouldBeGreaterThan(0);
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
        const string missingPromptJson = """{"sender": "test-user"}""";

        // Act
        await fixture.SendRawMessageAsync(missingPromptJson);

        // Wait for processing
        await Task.Delay(1000);

        // Assert - Message should be in dead-letter queue
        var deadLetterMessages = await fixture.GetDeadLetterMessagesAsync();
        deadLetterMessages.ShouldNotBeEmpty();

        var dlMessage = deadLetterMessages.First();
        dlMessage.DeadLetterReason.ShouldBe("MalformedMessage");
    }

    [Fact]
    public async Task SendPrompt_SameSourceId_SameChatIdThreadId()
    {
        // Arrange
        var sourceId = $"test-{Guid.NewGuid():N}";
        const string sender = "test-user";

        // Act - Send two prompts with the same sourceId
        await fixture.SendPromptAsync("First message", sender, sourceId);
        await Task.Delay(300);
        await fixture.SendPromptAsync("Second message", sender, sourceId);

        // Collect both prompts
        var prompts = new List<ChatPrompt>();
        var readTask = Task.Run(async () =>
        {
            await foreach (var p in _messengerClient.ReadPrompts(0, _cts.Token))
            {
                prompts.Add(p);
                if (prompts.Count >= 2) break;
            }
        });

        await Task.WhenAny(readTask, Task.Delay(10000));

        // Assert - Both prompts have the same chatId and threadId
        prompts.Count.ShouldBe(2);
        prompts[0].ChatId.ShouldBe(prompts[1].ChatId);
        prompts[0].ThreadId.ShouldBe(prompts[1].ThreadId);
    }

    [Fact]
    public async Task SendPrompt_DifferentSourceIds_DifferentChatIds()
    {
        // Arrange
        var sourceId1 = $"test-{Guid.NewGuid():N}";
        var sourceId2 = $"test-{Guid.NewGuid():N}";
        const string sender = "test-user";

        // Act - Send two prompts with different sourceIds
        await fixture.SendPromptAsync("First source message", sender, sourceId1);
        await Task.Delay(300);
        await fixture.SendPromptAsync("Second source message", sender, sourceId2);

        // Collect both prompts
        var prompts = new List<ChatPrompt>();
        var readTask = Task.Run(async () =>
        {
            await foreach (var p in _messengerClient.ReadPrompts(0, _cts.Token))
            {
                prompts.Add(p);
                if (prompts.Count >= 2) break;
            }
        });

        await Task.WhenAny(readTask, Task.Delay(10000));

        // Assert - Prompts have different chatIds
        prompts.Count.ShouldBe(2);
        prompts[0].ChatId.ShouldNotBe(prompts[1].ChatId);
    }

    private static async IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> CreateResponseStream(
        AgentKey key,
        string responseText)
    {
        await Task.CompletedTask;

        yield return (key, new AgentResponseUpdate
        {
            MessageId = "msg-1",
            Contents = [new TextContent(responseText)]
        }, null);

        yield return (key, new AgentResponseUpdate
        {
            MessageId = "msg-1",
            Contents = [new StreamCompleteContent()]
        }, new AiResponse { Content = responseText });
    }
}

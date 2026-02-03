# Service Bus Integration Tests Plan

## Summary

Add comprehensive integration tests for Azure Service Bus messaging components using Testcontainers with the Azure Service Bus emulator. Tests verify end-to-end flows: sending prompts to the queue, processing them through `ServiceBusProcessorHost`, and receiving responses via `ServiceBusResponseWriter`. The fixture composes `RedisFixture` for `ServiceBusSourceMapper` dependencies.

## Files

> **Note**: This is the canonical file list.

### Files to Edit
- `Tests/Tests.csproj`

### Files to Create
- `Tests/Integration/Fixtures/ServiceBusConfig.json`
- `Tests/Integration/Fixtures/ServiceBusFixture.cs`
- `Tests/Integration/Messaging/ServiceBusIntegrationTests.cs`

## Code Context

### Existing Test Fixture Patterns

**RedisFixture** (`Tests/Integration/Fixtures/RedisFixture.cs:7-37`):
- Implements `IAsyncLifetime` for xUnit lifecycle management
- Uses `ContainerBuilder` from Testcontainers to create Redis container
- Exposes `Connection` (`IConnectionMultiplexer`) and `ConnectionString` properties
- Pattern: `new ContainerBuilder("image:tag").WithPortBinding(port, true).WithWaitStrategy(...).Build()`

**QBittorrentFixture** (`Tests/Integration/Fixtures/QBittorrentFixture.cs:8-149`):
- Similar `IAsyncLifetime` pattern with container lifecycle
- Shows complex setup in `InitializeAsync()` with retry logic and configuration
- Creates client instances via `CreateClient()` method

**TelegramBotFixture** (`Tests/Integration/Fixtures/TelegramBotFixture.cs:10-298`):
- Uses WireMock for HTTP mocking instead of real container
- Shows pattern of `Reset()` method for test isolation
- Demonstrates helper method pattern like `CreateTextMessageUpdate()`

### Integration Test Patterns

**RedisScheduleStoreTests** (`Tests/Integration/StateManagers/RedisScheduleStoreTests.cs:8-220`):
- Uses `IClassFixture<RedisFixture>` with primary constructor injection
- Implements `IAsyncLifetime` for per-test cleanup
- Pattern: `public class Tests(Fixture fixture) : IClassFixture<Fixture>, IAsyncLifetime`
- Test isolation via unique IDs: `$"test_{Guid.NewGuid():N}"`

**TelegramBotChatMessengerClientTests** (`Tests/Integration/Clients/TelegramBotChatMessengerClientTests.cs:11`):
- Primary constructor: `public class Tests(Fixture fixture) : IClassFixture<Fixture>`
- Helper methods for creating test data streams

### Service Bus Components Under Test

**ServiceBusProcessorHost** (`Infrastructure/Clients/Messaging/ServiceBusProcessorHost.cs:9-81`):
- `BackgroundService` that processes messages from `ServiceBusProcessor`
- `ProcessMessageAsync` handler at line 31-71:
  - Deserializes JSON to `ServiceBusPromptMessage`
  - Extracts `sourceId` from `ApplicationProperties` (generates UUID if missing)
  - Extracts optional `agentId` from `ApplicationProperties`
  - Calls `messengerClient.EnqueueReceivedMessageAsync()`
  - Dead-letters on malformed JSON with reason "DeserializationError"
  - Dead-letters on missing prompt field with reason "MalformedMessage"

**ServiceBusChatMessengerClient** (`Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs:14-132`):
- Constructor: `(ServiceBusSourceMapper, ServiceBusResponseWriter, ILogger<>, string defaultAgentId)`
- `EnqueueReceivedMessageAsync(prompt, sender, sourceId, agentId, ct)` at line 101-131:
  - Calls `sourceMapper.GetOrCreateMappingAsync()` to get `chatId`/`threadId`
  - Stores `sourceId` mapping in `_chatIdToSourceId` dictionary
  - Creates `ChatPrompt` and writes to internal channel
- `ReadPrompts(timeout, ct)` at line 27-35: yields prompts from internal channel
- `ProcessResponseStreamAsync(updates, ct)` at line 37-74: accumulates responses and writes via `responseWriter`

**ServiceBusSourceMapper** (`Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs:10-65`):
- Constructor: `(IConnectionMultiplexer, IThreadStateStore, ILogger<>)`
- `GetOrCreateMappingAsync(sourceId, agentId, ct)` at line 17-62:
  - Redis key format: `sb-source:{agentId}:{sourceId}`
  - Creates `TopicMetadata` and stores via `threadStateStore.SaveTopicAsync()`
  - Returns `(ChatId, ThreadId, TopicId, IsNew)` tuple

**ServiceBusResponseWriter** (`Infrastructure/Clients/Messaging/ServiceBusResponseWriter.cs:7-49`):
- Constructor: `(ServiceBusSender, ILogger<>)`
- `WriteResponseAsync(sourceId, agentId, response, ct)` at line 11-40:
  - Creates `ServiceBusResponseMessage` record with `SourceId`, `Response`, `AgentId`, `CompletedAt`
  - Serializes to JSON and sends via `ServiceBusSender`

**ServiceBusPromptMessage** (`Domain/DTOs/ServiceBusPromptMessage.cs:7-14`):
```csharp
public record ServiceBusPromptMessage
{
    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [JsonPropertyName("sender")]
    public required string Sender { get; init; }
}
```

### DI Registration Pattern

**InjectorModule** (`Agent/Modules/InjectorModule.cs:174-206`):
```csharp
private IServiceCollection AddServiceBusClient(ServiceBusSettings sbSettings, string defaultAgentId)
{
    return services
        .AddSingleton(_ => new ServiceBusClient(sbSettings.ConnectionString))
        .AddSingleton(sp => client.CreateProcessor(sbSettings.PromptQueueName, options))
        .AddSingleton(sp => client.CreateSender(sbSettings.ResponseQueueName))
        .AddSingleton<ServiceBusSourceMapper>()
        .AddSingleton(sp => new ServiceBusResponseWriter(...))
        .AddSingleton(sp => new ServiceBusChatMessengerClient(...))
        .AddHostedService<ServiceBusProcessorHost>();
}
```

### Contracts

**IThreadStateStore** (`Domain/Contracts/IThreadStateStore.cs:7-20`):
- `SaveTopicAsync(TopicMetadata topic)` - used by ServiceBusSourceMapper
- `GetTopicByChatIdAndThreadIdAsync(agentId, chatId, threadId, ct)` - for verification

**TopicMetadata** (`Domain/DTOs/WebChat/TopicMetadata.cs`):
```csharp
public record TopicMetadata(
    string TopicId,
    long ChatId,
    long ThreadId,
    string AgentId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastMessageAt);
```

## External Context

### Testcontainers.ServiceBus NuGet Package

**Installation**:
```shell
dotnet add package Testcontainers.ServiceBus --version 4.10.0
```

**ServiceBusBuilder Usage**:
```csharp
var container = new ServiceBusBuilder()
    .WithAcceptLicenseAgreement(true)
    .WithConfig("path/to/config.json")
    .Build();

await container.StartAsync();

var connectionString = container.GetConnectionString();

await using var client = new ServiceBusClient(connectionString);
var sender = client.CreateSender("queue-name");
var receiver = client.CreateReceiver("queue-name");
```

**Key Methods**:
- `WithAcceptLicenseAgreement(bool)` - Required, sets `ACCEPT_EULA` environment variable
- `WithConfig(string)` - Maps local JSON config to container's config path
- `GetConnectionString()` - Returns connection string for `ServiceBusClient`

**Ports**: AMQP port 5672, HTTP port 5300

### Azure Service Bus Emulator Config.json Format

```json
{
  "UserConfig": {
    "Namespaces": [
      {
        "Name": "sbemulatorns",
        "Queues": [
          {
            "Name": "queue-name",
            "Properties": {
              "DeadLetteringOnMessageExpiration": false,
              "DefaultMessageTimeToLive": "PT1H",
              "LockDuration": "PT1M",
              "MaxDeliveryCount": 3
            }
          }
        ]
      }
    ],
    "Logging": {
      "Type": "File"
    }
  }
}
```

### Azure.Messaging.ServiceBus Client Usage

**Sending Messages**:
```csharp
var message = new ServiceBusMessage(BinaryData.FromString(json))
{
    ContentType = "application/json"
};
message.ApplicationProperties["sourceId"] = "source-123";
await sender.SendMessageAsync(message);
```

**Receiving Messages**:
```csharp
var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30));
var body = received.Body.ToString();
await receiver.CompleteMessageAsync(received);
```

**Dead-Letter Queue**:
```csharp
var dlqReceiver = client.CreateReceiver("queue-name", new ServiceBusReceiverOptions
{
    SubQueue = SubQueue.DeadLetter
});
var deadLettered = await dlqReceiver.ReceiveMessageAsync();
var reason = deadLettered.DeadLetterReason;
```

## Architectural Narrative

### Task

Implement integration tests for Azure Service Bus messaging components using Testcontainers with the Service Bus emulator. The tests verify the full message lifecycle: sending prompts to the prompt queue, processing them through `ServiceBusProcessorHost` which calls `ServiceBusChatMessengerClient.EnqueueReceivedMessageAsync()`, and receiving responses on the response queue via `ServiceBusResponseWriter`.

### Architecture

The Service Bus integration consists of:

1. **ServiceBusProcessorHost** (`BackgroundService`) - Subscribes to prompt queue, deserializes messages, extracts `sourceId`/`agentId` from application properties, delegates to `ServiceBusChatMessengerClient`
2. **ServiceBusChatMessengerClient** - Manages source-to-chat mapping via `ServiceBusSourceMapper`, enqueues prompts for processing, accumulates and writes responses
3. **ServiceBusSourceMapper** - Persists sourceId-to-chatId mappings in Redis, creates `TopicMetadata` via `IThreadStateStore`
4. **ServiceBusResponseWriter** - Sends response messages to response queue

### Selected Context

| File | Provides |
|------|----------|
| `Tests/Integration/Fixtures/RedisFixture.cs` | `IConnectionMultiplexer`, pattern for container fixtures |
| `Infrastructure/Clients/Messaging/ServiceBusProcessorHost.cs` | Message processing logic, dead-letter behavior |
| `Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs` | Prompt enqueuing, response accumulation |
| `Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs` | Source-to-chat ID mapping |
| `Infrastructure/Clients/Messaging/ServiceBusResponseWriter.cs` | Response queue writing |
| `Domain/DTOs/ServiceBusPromptMessage.cs` | Message schema (prompt, sender) |

### Relationships

```
ServiceBusFixture
    ├── composes RedisFixture (for ServiceBusSourceMapper)
    ├── starts ServiceBusContainer (for queues)
    └── creates ServiceBusClient, ServiceBusSender, ServiceBusReceiver

ServiceBusIntegrationTests
    └── uses ServiceBusFixture
        ├── SendPromptAsync() → sender.SendMessageAsync()
        ├── CreateMessengerClient() → ServiceBusChatMessengerClient
        ├── CreateProcessorHost() → ServiceBusProcessorHost
        ├── ReceiveResponseAsync() → receiver.ReceiveMessageAsync()
        └── GetDeadLetterMessagesAsync() → dlqReceiver.ReceiveMessagesAsync()
```

### External Context

- Testcontainers.ServiceBus 4.10.0 provides `ServiceBusBuilder` for emulator container
- Azure Service Bus emulator requires `ACCEPT_EULA=Y` and config.json with queue definitions
- `ServiceBusClient.GetConnectionString()` returns emulator connection string
- Dead-letter queue accessed via `SubQueue.DeadLetter` option

### Implementation Notes

1. **Fixture Composition**: `ServiceBusFixture` creates and owns a `RedisFixture` instance, not using xUnit's `IClassFixture` composition to avoid startup order issues

2. **Test Isolation**: Each test generates unique `sourceId` using `Guid.NewGuid().ToString("N")` to prevent cross-test interference within the shared fixture

3. **Processor Lifecycle**: Tests must start `ServiceBusProcessorHost` via `StartAsync()` before sending messages and stop via `StopAsync()` after

4. **Response Accumulation**: `ServiceBusChatMessengerClient` accumulates text content until `StreamCompleteContent` is received, then writes the full response

5. **Dead-Letter Verification**: Use separate `ServiceBusReceiver` with `SubQueue.DeadLetter` option to verify dead-lettered messages

6. **Config Path**: Config.json must be in the same directory as the test assembly at runtime, use `CopyToOutputDirectory="PreserveNewest"` in csproj

7. **IThreadStateStore Mock**: Use `Mock<IThreadStateStore>` since we only need `SaveTopicAsync()` to complete without error for these tests

### Ambiguities

**Resolved**: Whether to use full DI container or manual wiring
- Decision: Manual wiring in fixture to match existing test patterns and provide better control over component lifecycle

**Resolved**: Whether to test response writing through full message processing or isolate
- Decision: Full integration test with simulated agent response stream to verify end-to-end flow

### Requirements

1. `ServiceBusFixture` implements `IAsyncLifetime` and starts both Service Bus emulator and Redis containers
2. `ServiceBusConfig.json` defines `agent-prompts` and `agent-responses` queues
3. Test `SendPrompt_ValidMessage_ProcessedAndResponseWritten` verifies happy path from prompt to response
4. Test `SendPrompt_MissingSourceId_GeneratesUuidAndProcesses` verifies auto-generated sourceId behavior
5. Test `SendPrompt_MalformedJson_DeadLettered` verifies dead-letter with reason "DeserializationError"
6. Test `SendPrompt_MissingPromptField_DeadLettered` verifies dead-letter with reason "MalformedMessage"
7. Test `SendPrompt_SameSourceId_SameChatIdThreadId` verifies conversation continuity
8. Test `SendPrompt_DifferentSourceIds_DifferentChatIds` verifies source isolation

### Constraints

- Must use Testcontainers.ServiceBus 4.10.0 (matches existing Testcontainers version)
- Must compose `RedisFixture` for `ServiceBusSourceMapper` Redis dependency
- Must use `IClassFixture<ServiceBusFixture>` pattern for fixture sharing
- Must follow existing test naming convention: `{Method}_{Scenario}_{ExpectedResult}`
- Config.json must be embedded/copied to output directory

### Selected Approach

**Approach**: Composed Fixture with Manual Component Wiring

**Description**: Create `ServiceBusFixture` that internally creates a `RedisFixture` and starts the Service Bus emulator container. The fixture provides factory methods (`CreateMessengerClient()`, `CreateProcessorHost()`) that wire up components with real Redis and mocked `IThreadStateStore`. Tests send messages to the prompt queue, start the processor, simulate response streams, and verify responses on the response queue.

**Rationale**: This approach matches existing patterns (`TelegramBotFixture`, `QBittorrentFixture`) and provides full control over component lifecycle without requiring complex DI setup. Manual wiring is simpler to debug and modify than building a full service provider in tests.

**Trade-offs Accepted**: Tests create components manually rather than using DI, which means DI registration bugs won't be caught by these tests. However, unit tests already exist for the individual components.

## Implementation Plan

### Tests/Tests.csproj [edit]

**Purpose**: Add Testcontainers.ServiceBus NuGet package reference and configure ServiceBusConfig.json to be copied to output directory

**TOTAL CHANGES**: 2

**Changes**:
1. Add `<PackageReference Include="Testcontainers.ServiceBus" Version="4.10.0" />` after line 21 (after existing Testcontainers reference)
2. Add `<Content>` item to copy `ServiceBusConfig.json` to output directory, after line 51 (in the existing Content ItemGroup)

**Implementation Details**:
- Add package reference in the same ItemGroup as other test packages
- Version 4.10.0 matches existing Testcontainers version
- Use `<Content Include="..." CopyToOutputDirectory="PreserveNewest" />` pattern matching existing `xunit.runner.json`
- Specify `Link` attribute to preserve directory structure in output

**Migration Pattern**:
```xml
<!-- BEFORE (line 21): -->
    <PackageReference Include="Testcontainers" Version="4.10.0"/>

<!-- AFTER: -->
    <PackageReference Include="Testcontainers" Version="4.10.0"/>
    <PackageReference Include="Testcontainers.ServiceBus" Version="4.10.0"/>
```

```xml
<!-- BEFORE (line 51): -->
    <Content Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest"/>

<!-- AFTER: -->
    <Content Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest"/>
    <Content Include="Integration\Fixtures\ServiceBusConfig.json" CopyToOutputDirectory="PreserveNewest" Link="Integration\Fixtures\ServiceBusConfig.json"/>
```

**Dependencies**: None
**Provides**: `Testcontainers.ServiceBus` namespace with `ServiceBusBuilder` class, `ServiceBusConfig.json` in output directory

---

### Tests/Integration/Fixtures/ServiceBusConfig.json [create]

**Purpose**: Configuration file for Azure Service Bus emulator defining the prompt and response queues

**TOTAL CHANGES**: 1

**Changes**:
1. Create JSON configuration with two queues: `agent-prompts` and `agent-responses`

**Implementation Details**:
- Namespace name: `sbemulatorns` (standard emulator namespace)
- Queue names match `ServiceBusSettings.PromptQueueName` and `ResponseQueueName`
- MaxDeliveryCount: 3 (allows retry before dead-letter)
- LockDuration: PT1M (1 minute message lock)

**Reference Implementation**:
```json
{
  "UserConfig": {
    "Namespaces": [
      {
        "Name": "sbemulatorns",
        "Queues": [
          {
            "Name": "agent-prompts",
            "Properties": {
              "DeadLetteringOnMessageExpiration": false,
              "DefaultMessageTimeToLive": "PT1H",
              "LockDuration": "PT1M",
              "MaxDeliveryCount": 3
            }
          },
          {
            "Name": "agent-responses",
            "Properties": {
              "DeadLetteringOnMessageExpiration": false,
              "DefaultMessageTimeToLive": "PT1H",
              "LockDuration": "PT1M",
              "MaxDeliveryCount": 3
            }
          }
        ]
      }
    ],
    "Logging": {
      "Type": "File"
    }
  }
}
```

**Dependencies**: None
**Provides**: Queue configuration for `ServiceBusFixture`

---

### Tests/Integration/Fixtures/ServiceBusFixture.cs [create]

**Purpose**: Test fixture that manages Azure Service Bus emulator and Redis containers, provides factory methods for creating test components

**TOTAL CHANGES**: 1

**Changes**:
1. Create fixture class implementing `IAsyncLifetime` with container management and component factory methods

**Implementation Details**:
- Composes `RedisFixture` internally (not via xUnit fixture composition)
- Uses `ServiceBusBuilder` from Testcontainers.ServiceBus
- Exposes `ConnectionString` and `RedisConnection` properties
- Provides `SendPromptAsync()`, `ReceiveResponseAsync()`, `GetDeadLetterMessagesAsync()` helpers
- Provides `CreateMessengerClient()` and `CreateProcessorHost()` factory methods
- Uses `Mock<IThreadStateStore>` configured to complete `SaveTopicAsync()` successfully

**Reference Implementation**:
```csharp
using Azure.Messaging.ServiceBus;
using Domain.Contracts;
using Infrastructure.Clients.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using System.Text.Json;
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

        _serviceBusContainer = new ServiceBusBuilder()
            .WithAcceptLicenseAgreement(true)
            .WithConfig(configPath)
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

    public ServiceBusChatMessengerClient CreateMessengerClient()
    {
        var threadStateStoreMock = new Mock<IThreadStateStore>();
        threadStateStoreMock
            .Setup(s => s.SaveTopicAsync(It.IsAny<Domain.DTOs.WebChat.TopicMetadata>()))
            .Returns(Task.CompletedTask);

        var sourceMapper = new ServiceBusSourceMapper(
            RedisConnection,
            threadStateStoreMock.Object,
            NullLogger<ServiceBusSourceMapper>.Instance);

        var responseWriter = new ServiceBusResponseWriter(
            _responseSender,
            NullLogger<ServiceBusResponseWriter>.Instance);

        return new ServiceBusChatMessengerClient(
            sourceMapper,
            responseWriter,
            NullLogger<ServiceBusChatMessengerClient>.Instance,
            DefaultAgentId);
    }

    public ServiceBusProcessorHost CreateProcessorHost(ServiceBusChatMessengerClient client)
    {
        var processor = _serviceBusClient.CreateProcessor(PromptQueueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 1
        });

        return new ServiceBusProcessorHost(
            processor,
            client,
            NullLogger<ServiceBusProcessorHost>.Instance);
    }
}
```

**Test File**: N/A (this is a test fixture, not production code)

**Dependencies**: `Tests/Integration/Fixtures/ServiceBusConfig.json`, `Tests/Tests.csproj` (package reference)
**Provides**: `ServiceBusFixture` class with `CreateMessengerClient()`, `CreateProcessorHost()`, `SendPromptAsync()`, `ReceiveResponseAsync()`, `GetDeadLetterMessagesAsync()`

---

### Tests/Integration/Messaging/ServiceBusIntegrationTests.cs [create]

**Purpose**: Integration tests verifying Service Bus message processing end-to-end

**TOTAL CHANGES**: 1

**Changes**:
1. Create test class with 6 test methods covering happy path, error handling, and conversation continuity

**Implementation Details**:
- Uses `IClassFixture<ServiceBusFixture>` with primary constructor injection
- Implements `IAsyncLifetime` for per-test setup/teardown of processor host
- Each test uses unique `sourceId` for isolation
- Tests verify actual messages on response queue and dead-letter queue

**Reference Implementation**:
```csharp
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Domain.Agents;
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
        var prompts = new List<Domain.DTOs.ChatPrompt>();
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
        var agentKey = new AgentKey(prompts[0].ChatId, prompts[0].ThreadId, ServiceBusFixture.DefaultAgentId);
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
        var prompts = new List<Domain.DTOs.ChatPrompt>();
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
        var prompts = new List<Domain.DTOs.ChatPrompt>();
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
        var prompts = new List<Domain.DTOs.ChatPrompt>();
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
```

**Test File**: N/A (this IS the test file)

**Dependencies**: `Tests/Integration/Fixtures/ServiceBusFixture.cs`
**Provides**: Integration test coverage for Service Bus components

## Dependency Graph

> Files in the same phase can execute in parallel.

| Phase | File | Action | Depends On |
|-------|------|--------|------------|
| 1 | `Tests/Tests.csproj` | edit | — |
| 1 | `Tests/Integration/Fixtures/ServiceBusConfig.json` | create | — |
| 2 | `Tests/Integration/Fixtures/ServiceBusFixture.cs` | create | `Tests/Tests.csproj`, `Tests/Integration/Fixtures/ServiceBusConfig.json` |
| 3 | `Tests/Integration/Messaging/ServiceBusIntegrationTests.cs` | create | `Tests/Integration/Fixtures/ServiceBusFixture.cs` |

## Exit Criteria

### Test Commands
```bash
dotnet build Tests/Tests.csproj                                    # Verify compilation
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusIntegrationTests" --no-build   # Run integration tests
```

### Success Conditions
- [ ] All tests pass (exit code 0)
- [ ] `Testcontainers.ServiceBus` package resolves correctly
- [ ] `ServiceBusConfig.json` is copied to output directory
- [ ] `ServiceBusFixture` starts both Service Bus emulator and Redis containers
- [ ] `SendPrompt_ValidMessage_ProcessedAndResponseWritten` passes
- [ ] `SendPrompt_MissingSourceId_GeneratesUuidAndProcesses` passes
- [ ] `SendPrompt_MalformedJson_DeadLettered` passes
- [ ] `SendPrompt_MissingPromptField_DeadLettered` passes
- [ ] `SendPrompt_SameSourceId_SameChatIdThreadId` passes
- [ ] `SendPrompt_DifferentSourceIds_DifferentChatIds` passes
- [ ] All requirements satisfied
- [ ] All files implemented

### Verification Script
```bash
dotnet build Tests/Tests.csproj && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusIntegrationTests" --no-build
```

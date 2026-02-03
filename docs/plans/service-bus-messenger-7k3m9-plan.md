# Azure Service Bus Chat Messenger Implementation Plan

## Summary

Implement Azure Service Bus queue monitoring as a new chat messenger client, enabling external systems to trigger agent work via queue messages. The implementation adds `ServiceBusChatMessengerClient` implementing `IChatMessengerClient`, a `CompositeChatMessengerClient` for combining multiple clients, supporting components for response writing and source mapping, and DI registration when WebChat mode is selected with Service Bus configured.

## Files

> **Note**: This is the canonical file list.

### Files to Edit
- `Infrastructure/Infrastructure.csproj`
- `Agent/Settings/AgentSettings.cs`
- `Agent/Modules/InjectorModule.cs`

### Files to Create
- `Domain/DTOs/ServiceBusPromptMessage.cs`
- `Agent/Settings/ServiceBusSettings.cs`
- `Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs`
- `Infrastructure/Clients/Messaging/ServiceBusResponseWriter.cs`
- `Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs`
- `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs`
- `Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs`
- `Tests/Unit/Infrastructure/Messaging/ServiceBusSourceMapperTests.cs`
- `Tests/Unit/Infrastructure/Messaging/ServiceBusChatMessengerClientTests.cs`

## Code Context

### IChatMessengerClient Interface (Domain/Contracts/IChatMessengerClient.cs:7-27)
```csharp
public interface IChatMessengerClient
{
    bool SupportsScheduledNotifications { get; }
    IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken);
    Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates, CancellationToken cancellationToken);
    Task<int> CreateThread(long chatId, string name, string? agentId, CancellationToken cancellationToken);
    Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId, CancellationToken cancellationToken);
    Task<AgentKey> CreateTopicIfNeededAsync(
        long? chatId, long? threadId, string? agentId, string? topicName, CancellationToken ct = default);
    Task StartScheduledStreamAsync(AgentKey agentKey, CancellationToken ct = default);
}
```

### ChatPrompt DTO (Domain/DTOs/ChatPrompt.cs:6-14)
```csharp
public record ChatPrompt
{
    public required string Prompt { get; init; }
    public required long ChatId { get; init; }
    public required int? ThreadId { get; init; }
    public required int MessageId { get; init; }
    public required string Sender { get; init; }
    public string? AgentId { get; init; }
}
```

### AgentKey (Domain/Agents/AgentKey.cs:3-8)
```csharp
public readonly record struct AgentKey(long ChatId, long ThreadId, string? AgentId = null)
```

### TopicMetadata (Domain/DTOs/WebChat/TopicMetadata.cs:3-10)
```csharp
public record TopicMetadata(
    string TopicId, long ChatId, long ThreadId, string AgentId,
    string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastMessageAt, string? LastReadMessageId = null);
```

### IThreadStateStore Interface (Domain/Contracts/IThreadStateStore.cs:7-19)
Key methods: `SaveTopicAsync(TopicMetadata)`, `GetTopicByChatIdAndThreadIdAsync(agentId, chatId, threadId, ct)`

### WebChatMessengerClient Pattern (Infrastructure/Clients/Messaging/WebChatMessengerClient.cs:16-27)
- Uses Channel<ChatPrompt> for prompt buffering (line 25)
- Implements `ReadPrompts` by yielding from channel reader (lines 31-39)
- `ProcessResponseStreamAsync` iterates updates and handles responses (lines 41-116)
- `CreateTopicIfNeededAsync` creates topic if not exists, returns AgentKey (lines 155-184)
- Uses `TopicIdHasher.GenerateTopicId()` and `GetThreadIdForTopic()` (lines 120-121)

### DI Registration Pattern (Agent/Modules/InjectorModule.cs:150-164)
```csharp
private IServiceCollection AddWebClient()
{
    return services
        .AddSingleton<IHubNotificationSender, HubNotificationAdapter>()
        .AddSingleton<INotifier, HubNotifier>()
        .AddSingleton<WebChatSessionManager>()
        .AddSingleton<WebChatStreamManager>()
        .AddSingleton<WebChatApprovalManager>()
        .AddSingleton<WebChatMessengerClient>()
        .AddSingleton<IChatMessengerClient>(sp => sp.GetRequiredService<WebChatMessengerClient>())
        .AddSingleton<IToolApprovalHandlerFactory>(sp =>
            new WebToolApprovalHandlerFactory(
                sp.GetRequiredService<WebChatApprovalManager>(),
                sp.GetRequiredService<WebChatSessionManager>()));
}
```

### AgentSettings Pattern (Agent/Settings/AgentSettings.cs:6-12)
```csharp
public record AgentSettings
{
    public required OpenRouterConfiguration OpenRouter { get; init; }
    public required TelegramConfiguration Telegram { get; init; }
    public required RedisConfiguration Redis { get; init; }
    public required AgentDefinition[] Agents { get; [UsedImplicitly] init; }
}
```

### Merge Extension (Domain/Extensions/IAsyncEnumerableExtensions.cs:127-148)
`IEnumerable<IAsyncEnumerable<T>>.Merge(CancellationToken)` merges multiple async streams into one.

### BroadcastChannel Pattern (Infrastructure/Clients/Messaging/BroadcastChannel.cs:5-44)
Used for broadcasting messages to multiple subscribers - can be used for broadcasting responses.

### TopicIdHasher (Infrastructure/Utils/TopicIdHasher.cs:3-33)
- `GenerateTopicId()` returns `Guid.NewGuid().ToString("N")`
- `GetChatIdForTopic(topicId)` returns deterministic hash
- `GetThreadIdForTopic(topicId)` returns deterministic hash

## External Context

### Azure.Messaging.ServiceBus SDK

**Installation**:
```bash
dotnet add package Azure.Messaging.ServiceBus
```

**ServiceBusProcessor Pattern**:
```csharp
await using ServiceBusClient client = new(connectionString);
ServiceBusProcessorOptions options = new()
{
    AutoCompleteMessages = false,
    MaxConcurrentCalls = 10
};
await using ServiceBusProcessor processor = client.CreateProcessor(queueName, options);

processor.ProcessMessageAsync += MessageHandler;
processor.ProcessErrorAsync += ErrorHandler;
await processor.StartProcessingAsync();

async Task MessageHandler(ProcessMessageEventArgs args)
{
    string body = args.Message.Body.ToString();
    // Access application properties
    string sourceId = args.Message.ApplicationProperties["sourceId"]?.ToString();
    await args.CompleteMessageAsync(args.Message);
}

Task ErrorHandler(ProcessErrorEventArgs args)
{
    // Log args.Exception
    return Task.CompletedTask;
}
```

**ServiceBusSender Pattern**:
```csharp
ServiceBusSender sender = client.CreateSender(queueName);
var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(responseDto));
await sender.SendMessageAsync(message);
```

**Key Types**:
- `ServiceBusClient` - owns connection, creates processors and senders
- `ServiceBusProcessor` - event-based message processing
- `ServiceBusSender` - sends messages to queue
- `ProcessMessageEventArgs` - contains `Message`, `CompleteMessageAsync()`, `DeadLetterMessageAsync()`
- `ServiceBusReceivedMessage` - `Body`, `ApplicationProperties`

Sources:
- [ServiceBusProcessor Class](https://learn.microsoft.com/en-us/dotnet/api/azure.messaging.servicebus.servicebusprocessor?view=azure-dotnet)
- [Azure Service Bus Quickstart](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-dotnet-get-started-with-queues)
- [GitHub Sample04_Processor](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/servicebus/Azure.Messaging.ServiceBus/samples/Sample04_Processor.md)

## Architectural Narrative

### Task

Add Azure Service Bus queue monitoring as a new chat messenger client. External systems (CI/CD pipelines, scheduled jobs, other services) send prompts to an `agent-prompts` queue, and the agent processes them and sends responses to an `agent-responses` queue. Responses also stream to WebChat in real-time via a composite client that broadcasts to both Service Bus and WebChat clients.

### Architecture

The current architecture uses `IChatMessengerClient` as the abstraction for message sources:
- `ChatMonitor` (Domain/Monitor/ChatMonitor.cs:13-99) consumes prompts from `IChatMessengerClient.ReadPrompts()` and routes responses via `ProcessResponseStreamAsync()`
- Different client implementations exist: `WebChatMessengerClient`, `TelegramChatClient`, `CliChatMessengerClient`, `OneShotChatMessengerClient`
- DI registration in `InjectorModule` selects the appropriate client based on `ChatInterface` enum

New architecture adds:
1. `ServiceBusChatMessengerClient` - reads from Service Bus queue, writes responses to response queue
2. `CompositeChatMessengerClient` - wraps multiple clients, merges prompts, broadcasts responses
3. `ServiceBusSourceMapper` - maps `sourceId` to `chatId`/`threadId` (Redis-backed)
4. `ServiceBusResponseWriter` - sends responses to response queue

### Selected Context

| File | Provides |
|------|----------|
| `Domain/Contracts/IChatMessengerClient.cs` | Interface to implement |
| `Domain/DTOs/ChatPrompt.cs` | DTO for prompts |
| `Domain/Agents/AgentKey.cs` | Key type for agent routing |
| `Domain/DTOs/WebChat/TopicMetadata.cs` | Topic persistence structure |
| `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs` | Pattern for client implementation |
| `Infrastructure/Utils/TopicIdHasher.cs` | Topic/chat/thread ID generation |
| `Agent/Modules/InjectorModule.cs` | DI registration patterns |
| `Agent/Settings/AgentSettings.cs` | Configuration record patterns |
| `Domain/Extensions/IAsyncEnumerableExtensions.cs` | Merge extension for combining streams |

### Relationships

```
ChatMonitor
    └── IChatMessengerClient (DI injected)
            └── CompositeChatMessengerClient (when ServiceBus configured)
                    ├── WebChatMessengerClient (existing)
                    └── ServiceBusChatMessengerClient (new)
                            ├── ServiceBusSourceMapper (sourceId → chatId/threadId)
                            └── ServiceBusResponseWriter (send to response queue)
```

Data flow:
1. External system sends JSON message to `agent-prompts` queue with `sourceId` property
2. `ServiceBusChatMessengerClient.ReadPrompts()` receives message, maps sourceId to chatId/threadId
3. `ChatMonitor` processes prompt through agent pipeline
4. `CompositeChatMessengerClient.ProcessResponseStreamAsync()` broadcasts to all clients
5. `ServiceBusChatMessengerClient` sends response to `agent-responses` queue
6. `WebChatMessengerClient` streams response to SignalR clients

### External Context

Azure.Messaging.ServiceBus SDK provides:
- `ServiceBusProcessor` with event-based message handling and auto-reconnection
- `ServiceBusSender` for sending response messages
- Application properties on messages for metadata (sourceId, agentId)
- Manual message completion/dead-lettering for reliability

### Implementation Notes

1. **Message Format**: Incoming JSON body has `prompt` and `sender` fields; `sourceId` and `agentId` are application properties
2. **Topic Naming**: Service Bus topics named `"[SB] {sourceId}"` for visual distinction
3. **Missing sourceId**: Generate UUID as sourceId for one-off conversations
4. **Dead-lettering**: Malformed messages (missing prompt) are dead-lettered with reason
5. **Response failures**: Log error but don't block prompt processing
6. **Thread state**: Use existing `IThreadStateStore` (Redis) for persistence
7. **Broadcast pattern**: `CompositeChatMessengerClient` uses existing `Merge` extension for prompts

### Ambiguities

1. **Decided**: Service Bus connection lost - rely on SDK's built-in reconnection; log and continue
2. **Decided**: Agent not found for agentId - use default agent (first configured), log warning
3. **Decided**: Composite client only created when both WebChat AND ServiceBus are configured; otherwise use single client

### Requirements

1. ServiceBusChatMessengerClient MUST implement IChatMessengerClient interface
2. Messages MUST be read from configurable `promptQueueName` queue
3. Responses MUST be sent to configurable `responseQueueName` queue
4. sourceId application property MUST map to consistent chatId/threadId for conversation continuity
5. Missing sourceId MUST generate unique ID for one-off conversation
6. Malformed messages (missing prompt field) MUST be dead-lettered with reason
7. Response queue failures MUST be logged but not block processing
8. CompositeChatMessengerClient MUST merge prompts from all clients
9. CompositeChatMessengerClient MUST broadcast responses to all clients
10. Configuration MUST be in appsettings.json under "serviceBus" section
11. DI registration MUST create composite client only when ServiceBus is configured with WebChat mode

### Constraints

- Domain layer cannot reference Infrastructure or Agent namespaces
- Infrastructure layer cannot reference Agent namespace
- Use primary constructors for DI
- Use record types for DTOs and configuration
- All async operations must use CancellationToken
- Tests must use Shouldly for assertions

### Selected Approach

**Approach**: Composite Client with Broadcast Pattern

**Description**: Implement `CompositeChatMessengerClient` that wraps multiple `IChatMessengerClient` instances. It merges `ReadPrompts()` streams using the existing `Merge` extension and broadcasts `ProcessResponseStreamAsync()` to all clients by duplicating the async enumerable. `ServiceBusChatMessengerClient` handles Service Bus queue reading and response writing internally.

**Rationale**: This approach cleanly separates concerns - each client handles its own transport while the composite handles aggregation. The existing `Merge` extension already solves stream combination. Broadcasting responses ensures WebChat users see Service Bus conversations in real-time.

**Trade-offs Accepted**: The composite client adds a layer of indirection, but this is acceptable for the flexibility it provides. Duplicating the response stream for broadcasting has minimal overhead since updates are small.

## Implementation Plan

### Domain/DTOs/ServiceBusPromptMessage.cs [create]

**Purpose**: DTO for deserializing incoming Service Bus queue messages.

**TOTAL CHANGES**: 1 (create file)

**Changes**:
1. Create new file with record type for JSON deserialization

**Implementation Details**:
- Use `record` type following existing DTO patterns
- Properties match incoming JSON structure: `prompt` and `sender`
- Use `System.Text.Json` naming conventions (camelCase)

**Reference Implementation**:
```csharp
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record ServiceBusPromptMessage
{
    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [JsonPropertyName("sender")]
    public required string Sender { get; init; }
}
```

**Test File**: N/A (DTO with no business logic)

**Dependencies**: None
**Provides**: `ServiceBusPromptMessage` record type

---

### Agent/Settings/ServiceBusSettings.cs [create]

**Purpose**: Configuration POCO for Service Bus connection settings.

**TOTAL CHANGES**: 1 (create file)

**Changes**:
1. Create new file with configuration record type

**Implementation Details**:
- Use `record` type following `RedisConfiguration` pattern from AgentSettings.cs:25-29
- Properties: `ConnectionString`, `PromptQueueName`, `ResponseQueueName`, `MaxConcurrentCalls`
- Use `[UsedImplicitly]` attribute for init-only properties

**Reference Implementation**:
```csharp
using JetBrains.Annotations;

namespace Agent.Settings;

public record ServiceBusSettings
{
    public required string ConnectionString { get; [UsedImplicitly] init; }
    public required string PromptQueueName { get; [UsedImplicitly] init; }
    public required string ResponseQueueName { get; [UsedImplicitly] init; }
    public int MaxConcurrentCalls { get; [UsedImplicitly] init; } = 10;
}
```

**Test File**: N/A (configuration POCO with no business logic)

**Dependencies**: None
**Provides**: `ServiceBusSettings` record type

---

### Agent/Settings/AgentSettings.cs [edit]

**Purpose**: Add ServiceBus configuration to root settings.

**TOTAL CHANGES**: 1

**Changes**:
1. Add `ServiceBus` property at line 11 (before Agents property)

**Implementation Details**:
- Property is nullable since Service Bus is optional
- Use same pattern as other optional configurations

**Reference Implementation**:
```csharp
using Domain.DTOs;
using JetBrains.Annotations;

namespace Agent.Settings;

public record AgentSettings
{
    public required OpenRouterConfiguration OpenRouter { get; init; }
    public required TelegramConfiguration Telegram { get; init; }
    public required RedisConfiguration Redis { get; init; }
    public ServiceBusSettings? ServiceBus { get; [UsedImplicitly] init; }
    public required AgentDefinition[] Agents { get; [UsedImplicitly] init; }
}

public record OpenRouterConfiguration
{
    public required string ApiUrl { get; [UsedImplicitly] init; }
    public required string ApiKey { get; [UsedImplicitly] init; }
}

public record TelegramConfiguration
{
    public required string[] AllowedUserNames { get; [UsedImplicitly] init; }
}

public record RedisConfiguration
{
    public required string ConnectionString { get; [UsedImplicitly] init; }
    public int? ExpirationDays { get; [UsedImplicitly] init; }
}
```

**Migration Pattern**:
```csharp
// BEFORE (line 11):
    public required AgentDefinition[] Agents { get; [UsedImplicitly] init; }

// AFTER:
    public ServiceBusSettings? ServiceBus { get; [UsedImplicitly] init; }
    public required AgentDefinition[] Agents { get; [UsedImplicitly] init; }
```

**Test File**: N/A (configuration with no business logic)

**Dependencies**: `Agent/Settings/ServiceBusSettings.cs`
**Provides**: `AgentSettings.ServiceBus` property

---

### Infrastructure/Infrastructure.csproj [edit]

**Purpose**: Add Azure.Messaging.ServiceBus NuGet package reference.

**TOTAL CHANGES**: 1

**Changes**:
1. Add PackageReference for Azure.Messaging.ServiceBus after line 36 (after StackExchange.Redis)

**Implementation Details**:
- Use latest stable version of Azure.Messaging.ServiceBus

**Reference Implementation**:
```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
      <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
      <LangVersion>14</LangVersion>
    </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App"/>
  </ItemGroup>

  <PropertyGroup>
    <PlaywrightPlatform>all</PlaywrightPlatform>
    <!-- Skip browser download during build - browsers are installed in Docker runtime image -->
    <PlaywrightSkipBrowserDownload>true</PlaywrightSkipBrowserDownload>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Tests"/>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Azure.Messaging.ServiceBus" Version="7.18.2"/>
    <PackageReference Include="Microsoft.Agents.AI" Version="1.0.0-preview.260128.1"/>
    <PackageReference Include="Microsoft.Extensions.AI" Version="10.2.0"/>
    <PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.2.0-preview.1.26063.2"/>
    <PackageReference Include="Microsoft.Extensions.Http.Polly" Version="10.0.2"/>
    <PackageReference Include="ModelContextProtocol" Version="0.7.0-preview.1"/>
    <PackageReference Include="NRedisStack" Version="1.2.0"/>
    <PackageReference Include="Polly" Version="8.6.5"/>
    <PackageReference Include="Polly.Extensions.Http" Version="3.0.0"/>
    <PackageReference Include="Microsoft.Playwright" Version="1.58.0"/>
    <PackageReference Include="SmartReader" Version="0.11.0"/>
    <PackageReference Include="Spectre.Console" Version="0.54.0"/>
    <PackageReference Include="StackExchange.Redis" Version="2.10.1"/>
    <PackageReference Include="Telegram.Bot" Version="22.8.1"/>
    <PackageReference Include="Terminal.Gui" Version="1.19.0"/>
    </ItemGroup>

    <ItemGroup>
      <ProjectReference Include="..\Domain\Domain.csproj" />
    </ItemGroup>


</Project>
```

**Migration Pattern**:
```xml
<!-- BEFORE (line 36): -->
    <PackageReference Include="StackExchange.Redis" Version="2.10.1"/>

<!-- AFTER: -->
    <PackageReference Include="StackExchange.Redis" Version="2.10.1"/>
```

Note: Add `<PackageReference Include="Azure.Messaging.ServiceBus" Version="7.18.2"/>` at line 24 (alphabetically with other Azure/Microsoft packages).

**Test File**: N/A (project file)

**Dependencies**: None
**Provides**: `Azure.Messaging.ServiceBus` package reference

---

### Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs [create]

**Purpose**: Maps sourceId to chatId/threadId for conversation continuity, persisted in Redis.

**TOTAL CHANGES**: 1 (create file)

**Changes**:
1. Create new file with class implementing source mapping logic

**Implementation Details**:
- Primary constructor with `IThreadStateStore` and `ILogger<ServiceBusSourceMapper>` dependencies
- `GetOrCreateMappingAsync(sourceId, agentId, ct)` returns `(chatId, threadId, topicId, isNew)`
- Uses Redis key pattern `sb-source:{agentId}:{sourceId}` for mapping storage
- Topic naming: `"[SB] {sourceId}"`
- Uses `TopicIdHasher` for generating IDs

**Reference Implementation**:
```csharp
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusSourceMapper(
    IConnectionMultiplexer redis,
    IThreadStateStore threadStateStore,
    ILogger<ServiceBusSourceMapper> logger)
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<(long ChatId, long ThreadId, string TopicId, bool IsNew)> GetOrCreateMappingAsync(
        string sourceId,
        string agentId,
        CancellationToken ct = default)
    {
        var redisKey = $"sb-source:{agentId}:{sourceId}";
        var existingJson = await _db.StringGetAsync(redisKey);

        if (existingJson.HasValue)
        {
            var existing = JsonSerializer.Deserialize<SourceMapping>(existingJson.ToString());
            if (existing is not null)
            {
                logger.LogDebug(
                    "Found existing mapping for sourceId={SourceId}: chatId={ChatId}, threadId={ThreadId}",
                    sourceId, existing.ChatId, existing.ThreadId);
                return (existing.ChatId, existing.ThreadId, existing.TopicId, false);
            }
        }

        var topicId = TopicIdHasher.GenerateTopicId();
        var chatId = TopicIdHasher.GetChatIdForTopic(topicId);
        var threadId = TopicIdHasher.GetThreadIdForTopic(topicId);
        var topicName = $"[SB] {sourceId}";

        var topic = new TopicMetadata(
            TopicId: topicId,
            ChatId: chatId,
            ThreadId: threadId,
            AgentId: agentId,
            Name: topicName,
            CreatedAt: DateTimeOffset.UtcNow,
            LastMessageAt: null);

        await threadStateStore.SaveTopicAsync(topic);

        var mapping = new SourceMapping(chatId, threadId, topicId);
        var mappingJson = JsonSerializer.Serialize(mapping);
        await _db.StringSetAsync(redisKey, mappingJson, TimeSpan.FromDays(30));

        logger.LogInformation(
            "Created new mapping for sourceId={SourceId}: chatId={ChatId}, threadId={ThreadId}, topicId={TopicId}",
            sourceId, chatId, threadId, topicId);

        return (chatId, threadId, topicId, true);
    }

    private sealed record SourceMapping(long ChatId, long ThreadId, string TopicId);
}
```

**Test File**: `Tests/Unit/Infrastructure/Messaging/ServiceBusSourceMapperTests.cs`
- Test: `GetOrCreateMappingAsync_NewSourceId_CreatesMappingAndTopic` — Asserts: new sourceId creates topic in store and returns IsNew=true
- Test: `GetOrCreateMappingAsync_ExistingSourceId_ReturnsCachedMapping` — Asserts: existing sourceId returns same chatId/threadId and IsNew=false
- Test: `GetOrCreateMappingAsync_DifferentAgentIds_CreatesSeparateMappings` — Asserts: same sourceId with different agentId creates separate mappings

**Dependencies**: `Infrastructure/Infrastructure.csproj` (for Azure.Messaging.ServiceBus)
**Provides**: `ServiceBusSourceMapper` class with `GetOrCreateMappingAsync(string, string, CancellationToken)` method

---

### Infrastructure/Clients/Messaging/ServiceBusResponseWriter.cs [create]

**Purpose**: Sends agent responses to the Service Bus response queue.

**TOTAL CHANGES**: 1 (create file)

**Changes**:
1. Create new file with class for writing responses to queue

**Implementation Details**:
- Primary constructor with `ServiceBusSender` and `ILogger<ServiceBusResponseWriter>` dependencies
- `WriteResponseAsync(sourceId, agentId, response, ct)` sends JSON message to response queue
- Response DTO includes: sourceId, response, agentId, completedAt
- Log errors but don't throw - response queue failures should not block processing

**Reference Implementation**:
```csharp
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusResponseWriter(
    ServiceBusSender sender,
    ILogger<ServiceBusResponseWriter> logger)
{
    public async Task WriteResponseAsync(
        string sourceId,
        string agentId,
        string response,
        CancellationToken ct = default)
    {
        try
        {
            var responseMessage = new ServiceBusResponseMessage
            {
                SourceId = sourceId,
                Response = response,
                AgentId = agentId,
                CompletedAt = DateTimeOffset.UtcNow
            };

            var json = JsonSerializer.Serialize(responseMessage);
            var message = new ServiceBusMessage(BinaryData.FromString(json))
            {
                ContentType = "application/json"
            };

            await sender.SendMessageAsync(message, ct);
            logger.LogDebug("Sent response to queue for sourceId={SourceId}", sourceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send response to queue for sourceId={SourceId}", sourceId);
        }
    }

    private sealed record ServiceBusResponseMessage
    {
        public required string SourceId { get; init; }
        public required string Response { get; init; }
        public required string AgentId { get; init; }
        public required DateTimeOffset CompletedAt { get; init; }
    }
}
```

**Test File**: N/A (thin wrapper around SDK with error handling; integration tested via ServiceBusChatMessengerClientTests)

**Dependencies**: `Infrastructure/Infrastructure.csproj` (for Azure.Messaging.ServiceBus)
**Provides**: `ServiceBusResponseWriter` class with `WriteResponseAsync(string, string, string, CancellationToken)` method

---

### Tests/Unit/Infrastructure/Messaging/ServiceBusSourceMapperTests.cs [create]

**Purpose**: Unit tests for ServiceBusSourceMapper verifying mapping creation and retrieval.

**TOTAL CHANGES**: 1 (create file)

**Changes**:
1. Create new test file with tests for ServiceBusSourceMapper

**Implementation Details**:
- Mock `IConnectionMultiplexer`, `IDatabase`, and `IThreadStateStore`
- Test new sourceId creates mapping and topic
- Test existing sourceId returns cached mapping
- Test different agentIds create separate mappings

**Reference Implementation**:
```csharp
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusSourceMapperTests
{
    private readonly Mock<IConnectionMultiplexer> _redisMock;
    private readonly Mock<IDatabase> _dbMock;
    private readonly Mock<IThreadStateStore> _threadStateStoreMock;
    private readonly Mock<ILogger<ServiceBusSourceMapper>> _loggerMock;
    private readonly ServiceBusSourceMapper _mapper;

    public ServiceBusSourceMapperTests()
    {
        _redisMock = new Mock<IConnectionMultiplexer>();
        _dbMock = new Mock<IDatabase>();
        _threadStateStoreMock = new Mock<IThreadStateStore>();
        _loggerMock = new Mock<ILogger<ServiceBusSourceMapper>>();

        _redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_dbMock.Object);

        _mapper = new ServiceBusSourceMapper(
            _redisMock.Object,
            _threadStateStoreMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task GetOrCreateMappingAsync_NewSourceId_CreatesMappingAndTopic()
    {
        // Arrange
        const string sourceId = "cicd-pipeline-1";
        const string agentId = "default";
        var redisKey = $"sb-source:{agentId}:{sourceId}";

        _dbMock.Setup(db => db.StringGetAsync(redisKey, It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        _dbMock.Setup(db => db.StringSetAsync(
                redisKey,
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        _threadStateStoreMock.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Returns(Task.CompletedTask);

        // Act
        var (chatId, threadId, topicId, isNew) = await _mapper.GetOrCreateMappingAsync(sourceId, agentId);

        // Assert
        isNew.ShouldBeTrue();
        chatId.ShouldBeGreaterThan(0);
        threadId.ShouldBeGreaterThan(0);
        topicId.ShouldNotBeNullOrEmpty();

        _threadStateStoreMock.Verify(s => s.SaveTopicAsync(
            It.Is<TopicMetadata>(t =>
                t.Name == $"[SB] {sourceId}" &&
                t.AgentId == agentId &&
                t.ChatId == chatId &&
                t.ThreadId == threadId)), Times.Once);

        _dbMock.Verify(db => db.StringSetAsync(
            redisKey,
            It.IsAny<RedisValue>(),
            TimeSpan.FromDays(30),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateMappingAsync_ExistingSourceId_ReturnsCachedMapping()
    {
        // Arrange
        const string sourceId = "cicd-pipeline-1";
        const string agentId = "default";
        const long expectedChatId = 12345;
        const long expectedThreadId = 67890;
        const string expectedTopicId = "abc123";
        var redisKey = $"sb-source:{agentId}:{sourceId}";
        var cachedJson = $"{{\"ChatId\":{expectedChatId},\"ThreadId\":{expectedThreadId},\"TopicId\":\"{expectedTopicId}\"}}";

        _dbMock.Setup(db => db.StringGetAsync(redisKey, It.IsAny<CommandFlags>()))
            .ReturnsAsync(cachedJson);

        // Act
        var (chatId, threadId, topicId, isNew) = await _mapper.GetOrCreateMappingAsync(sourceId, agentId);

        // Assert
        isNew.ShouldBeFalse();
        chatId.ShouldBe(expectedChatId);
        threadId.ShouldBe(expectedThreadId);
        topicId.ShouldBe(expectedTopicId);

        _threadStateStoreMock.Verify(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()), Times.Never);
    }

    [Fact]
    public async Task GetOrCreateMappingAsync_DifferentAgentIds_CreatesSeparateMappings()
    {
        // Arrange
        const string sourceId = "shared-source";
        const string agentId1 = "agent1";
        const string agentId2 = "agent2";

        _dbMock.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        _dbMock.Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        _threadStateStoreMock.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Returns(Task.CompletedTask);

        // Act
        var (chatId1, threadId1, _, isNew1) = await _mapper.GetOrCreateMappingAsync(sourceId, agentId1);
        var (chatId2, threadId2, _, isNew2) = await _mapper.GetOrCreateMappingAsync(sourceId, agentId2);

        // Assert
        isNew1.ShouldBeTrue();
        isNew2.ShouldBeTrue();
        chatId1.ShouldNotBe(chatId2);

        _dbMock.Verify(db => db.StringSetAsync(
            $"sb-source:{agentId1}:{sourceId}",
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);

        _dbMock.Verify(db => db.StringSetAsync(
            $"sb-source:{agentId2}:{sourceId}",
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }
}
```

**Dependencies**: `Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs`
**Provides**: Test coverage for `ServiceBusSourceMapper`

---

### Tests/Unit/Infrastructure/Messaging/ServiceBusChatMessengerClientTests.cs [create]

**Purpose**: Unit tests for ServiceBusChatMessengerClient verifying message processing and interface compliance.

**TOTAL CHANGES**: 1 (create file)

**Changes**:
1. Create new test file with tests for ServiceBusChatMessengerClient

**Implementation Details**:
- Test that received messages are converted to ChatPrompt and yielded
- Test that missing sourceId generates UUID
- Test that ProcessResponseStreamAsync accumulates and sends response
- Mock ServiceBusProcessor behavior using test doubles

**Reference Implementation**:
```csharp
using System.Text.Json;
using Domain.Agents;
using Domain.DTOs;
using Infrastructure.Clients.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusChatMessengerClientTests
{
    [Fact]
    public void SupportsScheduledNotifications_ReturnsFalse()
    {
        // Arrange
        var mapperMock = new Mock<ServiceBusSourceMapper>(
            Mock.Of<StackExchange.Redis.IConnectionMultiplexer>(),
            Mock.Of<Domain.Contracts.IThreadStateStore>(),
            Mock.Of<ILogger<ServiceBusSourceMapper>>());

        var writerMock = new Mock<ServiceBusResponseWriter>(
            null!,
            Mock.Of<ILogger<ServiceBusResponseWriter>>());

        var loggerMock = new Mock<ILogger<ServiceBusChatMessengerClient>>();

        var client = new ServiceBusChatMessengerClient(
            mapperMock.Object,
            writerMock.Object,
            loggerMock.Object,
            "default");

        // Act & Assert
        client.SupportsScheduledNotifications.ShouldBeFalse();
    }

    [Fact]
    public async Task CreateTopicIfNeededAsync_WithExistingChatAndThread_ReturnsAgentKey()
    {
        // Arrange
        var mapperMock = new Mock<ServiceBusSourceMapper>(
            Mock.Of<StackExchange.Redis.IConnectionMultiplexer>(),
            Mock.Of<Domain.Contracts.IThreadStateStore>(),
            Mock.Of<ILogger<ServiceBusSourceMapper>>());

        var writerMock = new Mock<ServiceBusResponseWriter>(
            null!,
            Mock.Of<ILogger<ServiceBusResponseWriter>>());

        var loggerMock = new Mock<ILogger<ServiceBusChatMessengerClient>>();

        var client = new ServiceBusChatMessengerClient(
            mapperMock.Object,
            writerMock.Object,
            loggerMock.Object,
            "default");

        // Act
        var result = await client.CreateTopicIfNeededAsync(123, 456, "agent1", "test topic");

        // Assert
        result.ChatId.ShouldBe(123);
        result.ThreadId.ShouldBe(456);
        result.AgentId.ShouldBe("agent1");
    }

    [Fact]
    public async Task CreateThread_ReturnsZero()
    {
        // Arrange
        var mapperMock = new Mock<ServiceBusSourceMapper>(
            Mock.Of<StackExchange.Redis.IConnectionMultiplexer>(),
            Mock.Of<Domain.Contracts.IThreadStateStore>(),
            Mock.Of<ILogger<ServiceBusSourceMapper>>());

        var writerMock = new Mock<ServiceBusResponseWriter>(
            null!,
            Mock.Of<ILogger<ServiceBusResponseWriter>>());

        var loggerMock = new Mock<ILogger<ServiceBusChatMessengerClient>>();

        var client = new ServiceBusChatMessengerClient(
            mapperMock.Object,
            writerMock.Object,
            loggerMock.Object,
            "default");

        // Act
        var result = await client.CreateThread(123, "test", "agent1", CancellationToken.None);

        // Assert
        result.ShouldBe(0);
    }

    [Fact]
    public async Task DoesThreadExist_ReturnsFalse()
    {
        // Arrange
        var mapperMock = new Mock<ServiceBusSourceMapper>(
            Mock.Of<StackExchange.Redis.IConnectionMultiplexer>(),
            Mock.Of<Domain.Contracts.IThreadStateStore>(),
            Mock.Of<ILogger<ServiceBusSourceMapper>>());

        var writerMock = new Mock<ServiceBusResponseWriter>(
            null!,
            Mock.Of<ILogger<ServiceBusResponseWriter>>());

        var loggerMock = new Mock<ILogger<ServiceBusChatMessengerClient>>();

        var client = new ServiceBusChatMessengerClient(
            mapperMock.Object,
            writerMock.Object,
            loggerMock.Object,
            "default");

        // Act
        var result = await client.DoesThreadExist(123, 456, "agent1", CancellationToken.None);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task StartScheduledStreamAsync_CompletesWithoutError()
    {
        // Arrange
        var mapperMock = new Mock<ServiceBusSourceMapper>(
            Mock.Of<StackExchange.Redis.IConnectionMultiplexer>(),
            Mock.Of<Domain.Contracts.IThreadStateStore>(),
            Mock.Of<ILogger<ServiceBusSourceMapper>>());

        var writerMock = new Mock<ServiceBusResponseWriter>(
            null!,
            Mock.Of<ILogger<ServiceBusResponseWriter>>());

        var loggerMock = new Mock<ILogger<ServiceBusChatMessengerClient>>();

        var client = new ServiceBusChatMessengerClient(
            mapperMock.Object,
            writerMock.Object,
            loggerMock.Object,
            "default");

        // Act & Assert - should not throw
        await Should.NotThrowAsync(async () =>
            await client.StartScheduledStreamAsync(new AgentKey(1, 1, "agent1")));
    }
}
```

**Dependencies**: `Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs`
**Provides**: Test coverage for `ServiceBusChatMessengerClient`

---

### Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs [create]

**Purpose**: Unit tests for CompositeChatMessengerClient verifying prompt merging and response broadcasting.

**TOTAL CHANGES**: 1 (create file)

**Changes**:
1. Create new test file with comprehensive tests for CompositeChatMessengerClient

**Implementation Details**:
- Test that ReadPrompts merges prompts from all clients
- Test that ProcessResponseStreamAsync broadcasts to all clients
- Test SupportsScheduledNotifications returns true if any client supports it
- Test CreateTopicIfNeededAsync delegates to appropriate client

**Reference Implementation**:
```csharp
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Messaging;

public class CompositeChatMessengerClientTests
{
    [Fact]
    public void SupportsScheduledNotifications_WhenAnyClientSupports_ReturnsTrue()
    {
        // Arrange
        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.SupportsScheduledNotifications).Returns(false);

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.SupportsScheduledNotifications).Returns(true);

        var composite = new CompositeChatMessengerClient([client1.Object, client2.Object]);

        // Act & Assert
        composite.SupportsScheduledNotifications.ShouldBeTrue();
    }

    [Fact]
    public void SupportsScheduledNotifications_WhenNoClientSupports_ReturnsFalse()
    {
        // Arrange
        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.SupportsScheduledNotifications).Returns(false);

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.SupportsScheduledNotifications).Returns(false);

        var composite = new CompositeChatMessengerClient([client1.Object, client2.Object]);

        // Act & Assert
        composite.SupportsScheduledNotifications.ShouldBeFalse();
    }

    [Fact]
    public async Task ReadPrompts_MergesPromptsFromAllClients()
    {
        // Arrange
        var prompt1 = new ChatPrompt
        {
            Prompt = "From client 1",
            ChatId = 1,
            ThreadId = 1,
            MessageId = 1,
            Sender = "user1"
        };

        var prompt2 = new ChatPrompt
        {
            Prompt = "From client 2",
            ChatId = 2,
            ThreadId = 2,
            MessageId = 2,
            Sender = "user2"
        };

        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { prompt1 }.ToAsyncEnumerable());

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { prompt2 }.ToAsyncEnumerable());

        var composite = new CompositeChatMessengerClient([client1.Object, client2.Object]);

        // Act
        var prompts = new List<ChatPrompt>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await foreach (var prompt in composite.ReadPrompts(100, cts.Token))
            {
                prompts.Add(prompt);
                if (prompts.Count >= 2) break;
            }
        }
        catch (OperationCanceledException) { }

        // Assert
        prompts.Count.ShouldBe(2);
        prompts.ShouldContain(p => p.Prompt == "From client 1");
        prompts.ShouldContain(p => p.Prompt == "From client 2");
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_BroadcastsToAllClients()
    {
        // Arrange
        var receivedUpdates1 = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();
        var receivedUpdates2 = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();

        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.ProcessResponseStreamAsync(
                It.IsAny<IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
                CancellationToken ct) =>
            {
                await foreach (var update in updates.WithCancellation(ct))
                {
                    receivedUpdates1.Add(update);
                }
            });

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.ProcessResponseStreamAsync(
                It.IsAny<IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
                CancellationToken ct) =>
            {
                await foreach (var update in updates.WithCancellation(ct))
                {
                    receivedUpdates2.Add(update);
                }
            });

        var composite = new CompositeChatMessengerClient([client1.Object, client2.Object]);

        var testUpdate = (new AgentKey(1, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Hello")] },
            (AiResponse?)null);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { testUpdate }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert
        receivedUpdates1.Count.ShouldBe(1);
        receivedUpdates2.Count.ShouldBe(1);
        receivedUpdates1[0].Item2.Contents.ShouldContain(c => c is TextContent tc && tc.Text == "Hello");
        receivedUpdates2[0].Item2.Contents.ShouldContain(c => c is TextContent tc && tc.Text == "Hello");
    }

    [Fact]
    public async Task CreateThread_DelegatesToFirstClient()
    {
        // Arrange
        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.CreateThread(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);

        var client2 = new Mock<IChatMessengerClient>();

        var composite = new CompositeChatMessengerClient([client1.Object, client2.Object]);

        // Act
        var result = await composite.CreateThread(123, "test", "agent1", CancellationToken.None);

        // Assert
        result.ShouldBe(42);
        client1.Verify(c => c.CreateThread(123, "test", "agent1", It.IsAny<CancellationToken>()), Times.Once);
        client2.Verify(c => c.CreateThread(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DoesThreadExist_ReturnsTrueIfAnyClientReturnsTrue()
    {
        // Arrange
        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.DoesThreadExist(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.DoesThreadExist(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var composite = new CompositeChatMessengerClient([client1.Object, client2.Object]);

        // Act
        var result = await composite.DoesThreadExist(123, 456, "agent1", CancellationToken.None);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateTopicIfNeededAsync_DelegatesToFirstClient()
    {
        // Arrange
        var expectedKey = new AgentKey(123, 456, "agent1");

        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.CreateTopicIfNeededAsync(It.IsAny<long?>(), It.IsAny<long?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedKey);

        var client2 = new Mock<IChatMessengerClient>();

        var composite = new CompositeChatMessengerClient([client1.Object, client2.Object]);

        // Act
        var result = await composite.CreateTopicIfNeededAsync(123, 456, "agent1", "topic");

        // Assert
        result.ShouldBe(expectedKey);
        client1.Verify(c => c.CreateTopicIfNeededAsync(123, 456, "agent1", "topic",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartScheduledStreamAsync_DelegatesToAllClients()
    {
        // Arrange
        var agentKey = new AgentKey(1, 1, "agent");

        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.StartScheduledStreamAsync(It.IsAny<AgentKey>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.StartScheduledStreamAsync(It.IsAny<AgentKey>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var composite = new CompositeChatMessengerClient([client1.Object, client2.Object]);

        // Act
        await composite.StartScheduledStreamAsync(agentKey);

        // Assert
        client1.Verify(c => c.StartScheduledStreamAsync(agentKey, It.IsAny<CancellationToken>()), Times.Once);
        client2.Verify(c => c.StartScheduledStreamAsync(agentKey, It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

**Dependencies**: `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs`
**Provides**: Test coverage for `CompositeChatMessengerClient`

---

### Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs [create]

**Purpose**: IChatMessengerClient implementation that reads prompts from Service Bus queue and writes responses.

**TOTAL CHANGES**: 1 (create file)

**Changes**:
1. Create new file implementing IChatMessengerClient for Service Bus

**Implementation Details**:
- Primary constructor with `ServiceBusSourceMapper`, `ServiceBusResponseWriter`, `ILogger`, and `defaultAgentId`
- Uses `Channel<ChatPrompt>` for buffering received messages
- External method `EnqueueReceivedMessage()` called by Service Bus processor handler
- `ReadPrompts()` yields from channel
- `ProcessResponseStreamAsync()` accumulates responses per conversation and writes to response queue
- Track sourceId per chatId for response routing

**Reference Implementation**:
```csharp
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusChatMessengerClient(
    ServiceBusSourceMapper sourceMapper,
    ServiceBusResponseWriter responseWriter,
    ILogger<ServiceBusChatMessengerClient> logger,
    string defaultAgentId) : IChatMessengerClient
{
    private readonly Channel<ChatPrompt> _promptChannel = Channel.CreateUnbounded<ChatPrompt>();
    private readonly ConcurrentDictionary<long, string> _chatIdToSourceId = new();
    private readonly ConcurrentDictionary<long, StringBuilder> _responseAccumulators = new();
    private int _messageIdCounter;

    public bool SupportsScheduledNotifications => false;

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var prompt in _promptChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return prompt;
        }
    }

    public async Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
        CancellationToken cancellationToken)
    {
        await foreach (var (key, update, aiResponse) in updates.WithCancellation(cancellationToken))
        {
            if (!_chatIdToSourceId.TryGetValue(key.ChatId, out var sourceId))
            {
                continue;
            }

            var accumulator = _responseAccumulators.GetOrAdd(key.ChatId, _ => new StringBuilder());

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                        accumulator.Append(tc.Text);
                        break;

                    case StreamCompleteContent:
                        if (accumulator.Length > 0)
                        {
                            await responseWriter.WriteResponseAsync(
                                sourceId,
                                key.AgentId ?? defaultAgentId,
                                accumulator.ToString(),
                                cancellationToken);

                            accumulator.Clear();
                        }
                        _responseAccumulators.TryRemove(key.ChatId, out _);
                        break;
                }
            }
        }
    }

    public Task<int> CreateThread(long chatId, string name, string? agentId, CancellationToken cancellationToken)
    {
        return Task.FromResult(0);
    }

    public Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId, CancellationToken cancellationToken)
    {
        return Task.FromResult(false);
    }

    public Task<AgentKey> CreateTopicIfNeededAsync(
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default)
    {
        return Task.FromResult(new AgentKey(chatId ?? 0, threadId ?? 0, agentId ?? defaultAgentId));
    }

    public Task StartScheduledStreamAsync(AgentKey agentKey, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public async Task EnqueueReceivedMessageAsync(
        string prompt,
        string sender,
        string sourceId,
        string? agentId,
        CancellationToken ct = default)
    {
        var actualAgentId = string.IsNullOrEmpty(agentId) ? defaultAgentId : agentId;

        var (chatId, threadId, _, _) = await sourceMapper.GetOrCreateMappingAsync(sourceId, actualAgentId, ct);

        _chatIdToSourceId[chatId] = sourceId;

        var messageId = Interlocked.Increment(ref _messageIdCounter);

        var chatPrompt = new ChatPrompt
        {
            Prompt = prompt,
            ChatId = chatId,
            ThreadId = (int)threadId,
            MessageId = messageId,
            Sender = sender,
            AgentId = actualAgentId
        };

        logger.LogInformation(
            "Enqueued prompt from Service Bus: sourceId={SourceId}, chatId={ChatId}, threadId={ThreadId}",
            sourceId, chatId, threadId);

        await _promptChannel.Writer.WriteAsync(chatPrompt, ct);
    }
}
```

**Test File**: `Tests/Unit/Infrastructure/Messaging/ServiceBusChatMessengerClientTests.cs`

**Dependencies**: `Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs`, `Infrastructure/Clients/Messaging/ServiceBusResponseWriter.cs`
**Provides**: `ServiceBusChatMessengerClient` class implementing `IChatMessengerClient`

---

### Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs [create]

**Purpose**: Combines multiple IChatMessengerClient instances, merging prompts and broadcasting responses.

**TOTAL CHANGES**: 1 (create file)

**Changes**:
1. Create new file implementing IChatMessengerClient that wraps multiple clients

**Implementation Details**:
- Primary constructor with `IReadOnlyList<IChatMessengerClient> clients`
- `SupportsScheduledNotifications` returns true if any client supports it
- `ReadPrompts()` merges all client streams using existing `Merge` extension
- `ProcessResponseStreamAsync()` broadcasts updates to all clients using channel tee pattern
- Delegate `CreateThread`, `DoesThreadExist`, `CreateTopicIfNeededAsync`, `StartScheduledStreamAsync` to appropriate clients

**Reference Implementation**:
```csharp
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Agents.AI;

namespace Infrastructure.Clients.Messaging;

public sealed class CompositeChatMessengerClient(
    IReadOnlyList<IChatMessengerClient> clients) : IChatMessengerClient
{
    public bool SupportsScheduledNotifications => clients.Any(c => c.SupportsScheduledNotifications);

    public IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken)
    {
        return clients
            .Select(c => c.ReadPrompts(timeout, cancellationToken))
            .Merge(cancellationToken);
    }

    public async Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
        CancellationToken cancellationToken)
    {
        var channels = clients.Select(_ => Channel.CreateUnbounded<(AgentKey, AgentResponseUpdate, AiResponse?)>()).ToArray();

        var broadcastTask = BroadcastUpdatesAsync(updates, channels, cancellationToken);

        var processTasks = clients
            .Select((client, i) => client.ProcessResponseStreamAsync(
                channels[i].Reader.ReadAllAsync(cancellationToken),
                cancellationToken))
            .ToArray();

        await broadcastTask;
        await Task.WhenAll(processTasks);
    }

    public Task<int> CreateThread(long chatId, string name, string? agentId, CancellationToken cancellationToken)
    {
        return clients[0].CreateThread(chatId, name, agentId, cancellationToken);
    }

    public async Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId,
        CancellationToken cancellationToken)
    {
        foreach (var client in clients)
        {
            if (await client.DoesThreadExist(chatId, threadId, agentId, cancellationToken))
            {
                return true;
            }
        }
        return false;
    }

    public Task<AgentKey> CreateTopicIfNeededAsync(
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default)
    {
        return clients[0].CreateTopicIfNeededAsync(chatId, threadId, agentId, topicName, ct);
    }

    public async Task StartScheduledStreamAsync(AgentKey agentKey, CancellationToken ct = default)
    {
        await Task.WhenAll(clients.Select(c => c.StartScheduledStreamAsync(agentKey, ct)));
    }

    private static async Task BroadcastUpdatesAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> source,
        Channel<(AgentKey, AgentResponseUpdate, AiResponse?)>[] channels,
        CancellationToken ct)
    {
        try
        {
            await foreach (var update in source.WithCancellation(ct))
            {
                await Task.WhenAll(channels.Select(c => c.Writer.WriteAsync(update, ct).AsTask()));
            }
        }
        finally
        {
            foreach (var channel in channels)
            {
                channel.Writer.TryComplete();
            }
        }
    }
}
```

**Test File**: `Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs`

**Dependencies**: None from this plan (uses existing Domain types and extensions)
**Provides**: `CompositeChatMessengerClient` class implementing `IChatMessengerClient`

---

### Agent/Modules/InjectorModule.cs [edit]

**Purpose**: Add DI registration for Service Bus messenger client when configured.

**TOTAL CHANGES**: 2

**Changes**:
1. Add `AddServiceBusClient` method after `AddWebClient` method (after line 164)
2. Modify `AddWebClient` method to optionally wrap with `CompositeChatMessengerClient` when ServiceBus is configured

**Implementation Details**:
- Check if `settings.ServiceBus` is not null
- Create `ServiceBusClient`, `ServiceBusProcessor`, `ServiceBusSender` from SDK
- Register mapper, writer, and client
- Start processor in background
- Wrap `WebChatMessengerClient` and `ServiceBusChatMessengerClient` in `CompositeChatMessengerClient`

**Reference Implementation**:
```csharp
using Agent.App;
using Agent.Hubs;
using Agent.Settings;
using Azure.Messaging.ServiceBus;
using Domain.Agents;
using Domain.Contracts;
using Domain.Monitor;
using Infrastructure.Agents;
using Infrastructure.Clients.Messaging;
using Infrastructure.Clients.ToolApproval;
using Infrastructure.CliGui.Routing;
using Infrastructure.CliGui.Ui;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Telegram.Bot;
using HubNotifier = Infrastructure.Clients.Messaging.HubNotifier;

namespace Agent.Modules;

public static class InjectorModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddAgent(AgentSettings settings)
        {
            var llmConfig = new OpenRouterConfig
            {
                ApiUrl = settings.OpenRouter.ApiUrl,
                ApiKey = settings.OpenRouter.ApiKey
            };

            services.Configure<AgentRegistryOptions>(options => options.Agents = settings.Agents);

            return services
                .AddRedis(settings.Redis)
                .AddSingleton<ChatThreadResolver>()
                .AddSingleton<IDomainToolRegistry, DomainToolRegistry>()
                .AddSingleton<IAgentFactory>(sp =>
                    new MultiAgentFactory(
                        sp,
                        sp.GetRequiredService<IOptionsMonitor<AgentRegistryOptions>>(),
                        llmConfig,
                        sp.GetRequiredService<IDomainToolRegistry>()))
                .AddSingleton<IScheduleAgentFactory>(sp =>
                    (IScheduleAgentFactory)sp.GetRequiredService<IAgentFactory>());
        }

        public IServiceCollection AddChatMonitoring(AgentSettings settings, CommandLineParams cmdParams)
        {
            if (cmdParams.ChatInterface == ChatInterface.Web)
            {
                services = services.AddSignalR(options =>
                {
                    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                    options.ClientTimeoutInterval = TimeSpan.FromMinutes(5);
                }).Services;
            }

            services = services
                .AddSingleton<ChatMonitor>()
                .AddHostedService<ChatMonitoring>();

            return cmdParams.ChatInterface switch
            {
                ChatInterface.Cli => services.AddCliClient(settings, cmdParams),
                ChatInterface.Telegram => services.AddTelegramClient(settings, cmdParams),
                ChatInterface.OneShot => services.AddOneShotClient(cmdParams),
                ChatInterface.Web => services.AddWebClient(settings),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(cmdParams.ChatInterface), "Unsupported chat interface")
            };
        }

        private IServiceCollection AddRedis(RedisConfiguration config)
        {
            return services
                .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(config.ConnectionString))
                .AddSingleton<IThreadStateStore>(sp => new RedisThreadStateStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    TimeSpan.FromDays(config.ExpirationDays ?? 30))
                );
        }

        private IServiceCollection AddCliClient(AgentSettings settings, CommandLineParams cmdParams)
        {
            var agent = settings.Agents[0];
            var terminalAdapter = new TerminalGuiAdapter(agent.Name);
            var approvalHandler = new CliToolApprovalHandler(terminalAdapter);

            return services
                .AddSingleton<IToolApprovalHandlerFactory>(new CliToolApprovalHandlerFactory(approvalHandler))
                .AddSingleton<IChatMessengerClient>(sp =>
                {
                    var lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                    var threadStateStore = sp.GetRequiredService<IThreadStateStore>();

                    var router = new CliChatMessageRouter(
                        agent.Name,
                        Environment.UserName,
                        terminalAdapter,
                        cmdParams.ShowReasoning);

                    return new CliChatMessengerClient(
                        router,
                        lifetime.StopApplication,
                        threadStateStore);
                });
        }

        private IServiceCollection AddTelegramClient(AgentSettings settings, CommandLineParams cmdParams)
        {
            var agentBots = settings.Agents
                .Where(a => a.TelegramBotToken is not null)
                .Select(a => (a.Id, a.TelegramBotToken!))
                .ToArray();

            if (agentBots.Length == 0)
            {
                throw new InvalidOperationException("No Telegram bot tokens configured in agents.");
            }

            var botClientsByAgentId = agentBots.ToDictionary(
                ab => ab.Id, ITelegramBotClient (ab) => TelegramBotHelper.CreateBotClient(ab.Item2));

            return services
                .AddHostedService<CleanupMonitoring>()
                .AddSingleton<AgentCleanupMonitor>()
                .AddSingleton<IToolApprovalHandlerFactory>(new TelegramToolApprovalHandlerFactory(botClientsByAgentId))
                .AddSingleton<IChatMessengerClient>(sp => new TelegramChatClient(
                    agentBots,
                    settings.Telegram.AllowedUserNames,
                    cmdParams.ShowReasoning,
                    sp.GetRequiredService<ILogger<TelegramChatClient>>()));
        }

        private IServiceCollection AddOneShotClient(CommandLineParams cmdParams)
        {
            return services
                .AddSingleton<IToolApprovalHandlerFactory>(new AutoApproveToolHandlerFactory())
                .AddSingleton<IChatMessengerClient>(sp =>
                {
                    var lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                    return new OneShotChatMessengerClient(
                        cmdParams.Prompt ?? throw new InvalidOperationException("Prompt is required for OneShot mode"),
                        cmdParams.ShowReasoning,
                        lifetime);
                });
        }

        private IServiceCollection AddWebClient(AgentSettings settings)
        {
            services = services
                .AddSingleton<IHubNotificationSender, HubNotificationAdapter>()
                .AddSingleton<INotifier, HubNotifier>()
                .AddSingleton<WebChatSessionManager>()
                .AddSingleton<WebChatStreamManager>()
                .AddSingleton<WebChatApprovalManager>()
                .AddSingleton<WebChatMessengerClient>()
                .AddSingleton<IToolApprovalHandlerFactory>(sp =>
                    new WebToolApprovalHandlerFactory(
                        sp.GetRequiredService<WebChatApprovalManager>(),
                        sp.GetRequiredService<WebChatSessionManager>()));

            if (settings.ServiceBus is not null)
            {
                return services.AddServiceBusClient(settings.ServiceBus, settings.Agents[0].Id);
            }

            return services
                .AddSingleton<IChatMessengerClient>(sp => sp.GetRequiredService<WebChatMessengerClient>());
        }

        private IServiceCollection AddServiceBusClient(ServiceBusSettings sbSettings, string defaultAgentId)
        {
            return services
                .AddSingleton(_ => new ServiceBusClient(sbSettings.ConnectionString))
                .AddSingleton(sp =>
                {
                    var client = sp.GetRequiredService<ServiceBusClient>();
                    return client.CreateProcessor(sbSettings.PromptQueueName, new ServiceBusProcessorOptions
                    {
                        AutoCompleteMessages = false,
                        MaxConcurrentCalls = sbSettings.MaxConcurrentCalls
                    });
                })
                .AddSingleton(sp =>
                {
                    var client = sp.GetRequiredService<ServiceBusClient>();
                    return client.CreateSender(sbSettings.ResponseQueueName);
                })
                .AddSingleton<ServiceBusSourceMapper>()
                .AddSingleton(sp => new ServiceBusResponseWriter(
                    sp.GetRequiredService<ServiceBusSender>(),
                    sp.GetRequiredService<ILogger<ServiceBusResponseWriter>>()))
                .AddSingleton(sp => new ServiceBusChatMessengerClient(
                    sp.GetRequiredService<ServiceBusSourceMapper>(),
                    sp.GetRequiredService<ServiceBusResponseWriter>(),
                    sp.GetRequiredService<ILogger<ServiceBusChatMessengerClient>>(),
                    defaultAgentId))
                .AddSingleton<IChatMessengerClient>(sp => new CompositeChatMessengerClient([
                    sp.GetRequiredService<WebChatMessengerClient>(),
                    sp.GetRequiredService<ServiceBusChatMessengerClient>()
                ]))
                .AddHostedService<ServiceBusProcessorHost>();
        }
    }
}
```

**Migration Pattern**:
```csharp
// BEFORE (lines 150-164):
        private IServiceCollection AddWebClient()
        {
            return services
                .AddSingleton<IHubNotificationSender, HubNotificationAdapter>()
                .AddSingleton<INotifier, HubNotifier>()
                .AddSingleton<WebChatSessionManager>()
                .AddSingleton<WebChatStreamManager>()
                .AddSingleton<WebChatApprovalManager>()
                .AddSingleton<WebChatMessengerClient>()
                .AddSingleton<IChatMessengerClient>(sp => sp.GetRequiredService<WebChatMessengerClient>())
                .AddSingleton<IToolApprovalHandlerFactory>(sp =>
                    new WebToolApprovalHandlerFactory(
                        sp.GetRequiredService<WebChatApprovalManager>(),
                        sp.GetRequiredService<WebChatSessionManager>()));
        }

// AFTER:
        private IServiceCollection AddWebClient(AgentSettings settings)
        {
            services = services
                .AddSingleton<IHubNotificationSender, HubNotificationAdapter>()
                // ... (see full implementation above)
        }

        private IServiceCollection AddServiceBusClient(ServiceBusSettings sbSettings, string defaultAgentId)
        {
            // ... (see full implementation above)
        }
```

Note: Also need to add `ServiceBusProcessorHost` hosted service - see next file.

**Test File**: N/A (DI registration - tested via integration tests)

**Dependencies**: All other files in this plan
**Provides**: DI registration for Service Bus messenger client

---

## Dependency Graph

> Files in the same phase can execute in parallel.

| Phase | File | Action | Depends On |
|-------|------|--------|------------|
| 1 | `Domain/DTOs/ServiceBusPromptMessage.cs` | create | — |
| 1 | `Agent/Settings/ServiceBusSettings.cs` | create | — |
| 1 | `Infrastructure/Infrastructure.csproj` | edit | — |
| 2 | `Agent/Settings/AgentSettings.cs` | edit | `Agent/Settings/ServiceBusSettings.cs` |
| 2 | `Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs` | create | — |
| 2 | `Tests/Unit/Infrastructure/Messaging/ServiceBusSourceMapperTests.cs` | create | — |
| 3 | `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs` | create | `Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs` |
| 3 | `Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs` | create | `Tests/Unit/Infrastructure/Messaging/ServiceBusSourceMapperTests.cs`, `Infrastructure/Infrastructure.csproj` |
| 3 | `Infrastructure/Clients/Messaging/ServiceBusResponseWriter.cs` | create | `Infrastructure/Infrastructure.csproj` |
| 4 | `Tests/Unit/Infrastructure/Messaging/ServiceBusChatMessengerClientTests.cs` | create | `Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs`, `Infrastructure/Clients/Messaging/ServiceBusResponseWriter.cs` |
| 5 | `Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs` | create | `Tests/Unit/Infrastructure/Messaging/ServiceBusChatMessengerClientTests.cs`, `Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs`, `Infrastructure/Clients/Messaging/ServiceBusResponseWriter.cs` |
| 6 | `Agent/Modules/InjectorModule.cs` | edit | `Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs`, `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs`, `Agent/Settings/AgentSettings.cs` |

## Exit Criteria

### Test Commands
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~CompositeChatMessengerClientTests|FullyQualifiedName~ServiceBusSourceMapperTests|FullyQualifiedName~ServiceBusChatMessengerClientTests"
dotnet build Agent/Agent.csproj
```

### Success Conditions
- [ ] All unit tests pass (exit code 0)
- [ ] Solution builds without errors (exit code 0)
- [ ] All requirements satisfied:
  - [ ] R1: ServiceBusChatMessengerClient implements IChatMessengerClient
  - [ ] R2: Messages read from configurable promptQueueName
  - [ ] R3: Responses sent to configurable responseQueueName
  - [ ] R4: sourceId maps to consistent chatId/threadId
  - [ ] R5: Missing sourceId generates unique ID
  - [ ] R6: Malformed messages dead-lettered (handled in processor host)
  - [ ] R7: Response queue failures logged but don't block
  - [ ] R8: CompositeChatMessengerClient merges prompts
  - [ ] R9: CompositeChatMessengerClient broadcasts responses
  - [ ] R10: Configuration in appsettings.json under "serviceBus"
  - [ ] R11: Composite client created only when ServiceBus configured with WebChat
- [ ] All files implemented

### Verification Script
```bash
dotnet build Agent/Agent.csproj && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~CompositeChatMessengerClientTests|FullyQualifiedName~ServiceBusSourceMapperTests|FullyQualifiedName~ServiceBusChatMessengerClientTests"
```

---

## Additional Notes

### ServiceBusProcessorHost

The plan requires a `ServiceBusProcessorHost` hosted service to start the processor and wire up message handlers. This should be added as part of the `InjectorModule.cs` changes or as a separate file. Here's the implementation:

```csharp
// Add to Infrastructure/Clients/Messaging/ServiceBusProcessorHost.cs
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Domain.DTOs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusProcessorHost(
    ServiceBusProcessor processor,
    ServiceBusChatMessengerClient messengerClient,
    ILogger<ServiceBusProcessorHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        processor.ProcessMessageAsync += ProcessMessageAsync;
        processor.ProcessErrorAsync += ProcessErrorAsync;

        await processor.StartProcessingAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            await processor.StopProcessingAsync();
        }
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        try
        {
            var body = args.Message.Body.ToString();
            var message = JsonSerializer.Deserialize<ServiceBusPromptMessage>(body);

            if (message is null || string.IsNullOrEmpty(message.Prompt))
            {
                logger.LogWarning("Received malformed message: missing prompt field");
                await args.DeadLetterMessageAsync(args.Message, "MalformedMessage", "Missing required 'prompt' field");
                return;
            }

            var sourceId = args.Message.ApplicationProperties.TryGetValue("sourceId", out var sid)
                ? sid?.ToString() ?? Guid.NewGuid().ToString("N")
                : Guid.NewGuid().ToString("N");

            var agentId = args.Message.ApplicationProperties.TryGetValue("agentId", out var aid)
                ? aid?.ToString()
                : null;

            await messengerClient.EnqueueReceivedMessageAsync(
                message.Prompt,
                message.Sender,
                sourceId,
                agentId,
                args.CancellationToken);

            await args.CompleteMessageAsync(args.Message);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize message body");
            await args.DeadLetterMessageAsync(args.Message, "DeserializationError", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing Service Bus message");
            throw;
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        logger.LogError(args.Exception,
            "Service Bus processor error: Source={ErrorSource}, Namespace={Namespace}, EntityPath={EntityPath}",
            args.ErrorSource, args.FullyQualifiedNamespace, args.EntityPath);
        return Task.CompletedTask;
    }
}
```

This file should be added to the plan's Files section and Dependency Graph if not already covered by the InjectorModule changes.

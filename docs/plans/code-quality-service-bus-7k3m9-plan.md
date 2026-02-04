# Code Quality Plan: Service Bus Messaging Components

**File**: Multiple files in `Infrastructure/Clients/Messaging/`
**Analysis Date**: 2026-02-04

## Summary

Refactor the Azure Service Bus messaging implementation to improve single-responsibility, testability, and resilience. Split `ServiceBusChatMessengerClient` (136 lines doing 4 things) into focused components, extract message parsing from `ServiceBusProcessorHost`, add Polly retry to `ServiceBusResponseWriter`, and extract routing logic from `CompositeChatMessengerClient` into a dedicated router.

## Files

> **Note**: This is the canonical file list.

### Files to Edit
- `Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs`
- `Infrastructure/Clients/Messaging/ServiceBusProcessorHost.cs`
- `Infrastructure/Clients/Messaging/ServiceBusResponseWriter.cs`
- `Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs`
- `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs`
- `Agent/Modules/InjectorModule.cs`

### Files to Create
- `Domain/DTOs/ParsedServiceBusMessage.cs`
- `Domain/DTOs/ParseResult.cs`
- `Domain/Contracts/IMessageSourceRouter.cs`
- `Infrastructure/Clients/Messaging/ServiceBusMessageParser.cs`
- `Infrastructure/Clients/Messaging/ServiceBusPromptReceiver.cs`
- `Infrastructure/Clients/Messaging/ServiceBusResponseHandler.cs`
- `Infrastructure/Clients/Messaging/MessageSourceRouter.cs`
- `Tests/Unit/Infrastructure/Messaging/ServiceBusMessageParserTests.cs`
- `Tests/Unit/Infrastructure/Messaging/ServiceBusPromptReceiverTests.cs`
- `Tests/Unit/Infrastructure/Messaging/ServiceBusResponseHandlerTests.cs`
- `Tests/Unit/Infrastructure/Messaging/MessageSourceRouterTests.cs`
- `Tests/Unit/Infrastructure/Messaging/ServiceBusResponseWriterTests.cs`

## Code Context

### Current Structure Analysis (LSP-based)

**ServiceBusChatMessengerClient.cs** (lines 1-136):
- 4 private fields for state: `_promptChannel`, `_chatIdToSourceId`, `_responseAccumulators`, `_messageIdCounter`
- Multiple responsibilities: prompt enqueueing (lines 104-135), response accumulation (lines 38-76), source mapping (line 115), channel management (line 20)
- `EnqueueReceivedMessageAsync` (line 104) called only by `ServiceBusProcessorHost` (line 52)
- `_chatIdToSourceId` dictionary duplicates mapping state that should live in `ServiceBusSourceMapper`

**ServiceBusProcessorHost.cs** (lines 1-80):
- Inline JSON parsing (lines 34-35) mixed with processor lifecycle
- Error handling duplicated: `JsonException` (line 61), general `Exception` (line 66)
- `ProcessMessageAsync` (line 30) is 41 lines with parsing, validation, and orchestration

**ServiceBusResponseWriter.cs** (lines 1-49):
- Swallows all exceptions silently (line 36-38)
- No retry logic for transient failures
- `ServiceBusSender.SendMessageAsync` can throw `ServiceBusException` with `IsTransient=true`

**ServiceBusSourceMapper.cs** (lines 1-65):
- Only forward mapping: sourceId -> (chatId, threadId)
- No reverse lookup: chatId -> sourceId (needed for response routing)
- `_chatIdToSourceId` in `ServiceBusChatMessengerClient` is a workaround for this missing capability

**CompositeChatMessengerClient.cs** (lines 1-127):
- `GetClientsForSource` (line 94-97) has embedded routing logic
- Called from multiple places: `CreateTopicIfNeededAsync` (line 69), `StartScheduledStreamAsync` (line 81), `BroadcastUpdatesAsync` (line 110)
- Routing policy (WebUI receives all + source-specific) hardcoded in private method

### LSP Reference Analysis

| Symbol | References | External Consumers |
|--------|------------|-------------------|
| `ServiceBusChatMessengerClient` | 14 across 6 files | InjectorModule, ServiceBusProcessorHost, Tests |
| `EnqueueReceivedMessageAsync` | 2 | ServiceBusProcessorHost only |
| `ServiceBusSourceMapper` | 12 across 6 files | ServiceBusChatMessengerClient, InjectorModule, Tests |
| `GetClientsForSource` | 4 internal | Private method, no external refs |
| `IChatMessengerClient` | 41 across 16 files | ChatMonitor, ScheduleExecutor, Tests, all client implementations |

## LSP Analysis Summary

**Symbols Found**:
- Classes: 5 (ServiceBusChatMessengerClient, ServiceBusProcessorHost, ServiceBusResponseWriter, ServiceBusSourceMapper, CompositeChatMessengerClient)
- Methods: 23 total across all files
- Private nested types: 2 (SourceMapping, ServiceBusResponseMessage)

**Reference Analysis**:
- `EnqueueReceivedMessageAsync` - 2 references, only called by ServiceBusProcessorHost (candidate for moving to receiver component)
- `_chatIdToSourceId` - 2 internal references, duplicates state that should be in mapper
- `GetClientsForSource` - 4 internal references, private but embeds routing policy

**Unused Elements**: None found (all public members have external consumers)

## External Context

**Polly Retry Pattern** (already in Infrastructure.csproj):
```xml
<PackageReference Include="Polly" Version="8.6.5"/>
```

Polly 8.x uses `ResiliencePipeline` pattern:
```csharp
var pipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        ShouldHandle = new PredicateBuilder().Handle<ServiceBusException>(ex => ex.IsTransient),
        MaxRetryAttempts = 3,
        Delay = TimeSpan.FromSeconds(1),
        BackoffType = DelayBackoffType.Exponential
    })
    .Build();

await pipeline.ExecuteAsync(async ct => await sender.SendMessageAsync(message, ct), ct);
```

**Azure Service Bus Transient Errors**:
- `ServiceBusException.IsTransient` indicates retriable errors
- `TimeoutException` from connection issues
- Recommended: exponential backoff with 3 retries

## Architectural Narrative

### Task

Refactor Service Bus messaging to achieve single-responsibility components with improved testability and resilience:
1. Split `ServiceBusChatMessengerClient` into facade + focused components
2. Extract message parsing into testable `ServiceBusMessageParser`
3. Add Polly retry with exponential backoff to `ServiceBusResponseWriter`
4. Add reverse lookup to `ServiceBusSourceMapper` (rename to `ServiceBusConversationMapper`)
5. Extract routing logic from `CompositeChatMessengerClient` to `MessageSourceRouter`

### Architecture

Current message flow:
```
ServiceBusProcessor (Azure SDK)
    ↓ ProcessMessageAsync event
ServiceBusProcessorHost.ProcessMessageAsync (parsing + orchestration)
    ↓ EnqueueReceivedMessageAsync
ServiceBusChatMessengerClient (state + channel + mapping)
    ↓ _promptChannel
ChatMonitor.ReadPrompts
    ↓ processes prompt
ChatMonitor.ProcessResponseStreamAsync
    ↓ IAsyncEnumerable
ServiceBusChatMessengerClient.ProcessResponseStreamAsync (accumulation)
    ↓ WriteResponseAsync
ServiceBusResponseWriter (send to queue)
```

Target message flow:
```
ServiceBusProcessor (Azure SDK)
    ↓ ProcessMessageAsync event
ServiceBusProcessorHost (lifecycle only)
    ↓ delegates to
ServiceBusMessageParser.Parse → ParseResult
    ↓ if ParseSuccess
ServiceBusPromptReceiver.EnqueueAsync
    ↓ _promptChannel
ChatMonitor.ReadPrompts
    ↓ processes prompt
ChatMonitor.ProcessResponseStreamAsync
    ↓ IAsyncEnumerable
ServiceBusResponseHandler.ProcessAsync (accumulation)
    ↓ WriteResponseAsync (with Polly retry)
ServiceBusResponseWriter
```

### Selected Context

| File | Provides |
|------|----------|
| `Domain/Contracts/IChatMessengerClient.cs` | Interface that ServiceBusChatMessengerClient implements |
| `Domain/DTOs/ServiceBusPromptMessage.cs` | Existing DTO for deserializing queue messages |
| `Domain/DTOs/MessageSource.cs` | Enum with WebUi, ServiceBus, Telegram, Cli |
| `Agent/Modules/InjectorModule.cs` | DI registration at lines 172-204 |
| `Infrastructure/Infrastructure.csproj` | Already has Polly 8.6.5 package reference |

### Relationships

```
InjectorModule
  ├─ registers ServiceBusClient, ServiceBusProcessor, ServiceBusSender
  ├─ registers ServiceBusConversationMapper (renamed from ServiceBusSourceMapper)
  ├─ registers ServiceBusMessageParser
  ├─ registers ServiceBusPromptReceiver
  ├─ registers ServiceBusResponseWriter (with Polly)
  ├─ registers ServiceBusResponseHandler
  ├─ registers ServiceBusChatMessengerClient (thin facade)
  ├─ registers MessageSourceRouter
  └─ registers CompositeChatMessengerClient (uses router)

ServiceBusProcessorHost → ServiceBusMessageParser, ServiceBusPromptReceiver
ServiceBusPromptReceiver → ServiceBusConversationMapper
ServiceBusResponseHandler → ServiceBusPromptReceiver, ServiceBusResponseWriter
ServiceBusChatMessengerClient → ServiceBusPromptReceiver, ServiceBusResponseHandler
CompositeChatMessengerClient → IMessageSourceRouter
```

### External Context

- Polly 8.x ResiliencePipeline pattern for retry
- Azure.Messaging.ServiceBus exception handling patterns
- Project coding style: primary constructors, file-scoped namespaces, record types for DTOs

### Implementation Notes

1. **TDD approach**: Test files in earlier phases than production code
2. **Rename ServiceBusSourceMapper**: Keep backward compatibility during migration by updating all references
3. **ParseResult pattern**: Use discriminated union (abstract record with sealed subtypes) for type-safe parse results
4. **Polly 8.x**: Use ResiliencePipeline, not the older Policy-based API
5. **Preserve IChatMessengerClient interface**: ServiceBusChatMessengerClient remains the implementer, just becomes a thin facade

### Ambiguities

1. **Thread safety**: `ServiceBusConversationMapper._chatIdToSourceId` will be `ConcurrentDictionary` for thread-safe in-memory reverse lookup
2. **Retry exhaustion behavior**: Log error and don't block prompt processing (fail-open for resilience)
3. **CreateThread return value**: Kept as `Task.FromResult(0)` - Service Bus doesn't manage threads

### Requirements

1. `ServiceBusChatMessengerClient` must be < 40 lines as a thin facade
2. `ServiceBusMessageParser.Parse` must return `ParseResult` (ParseSuccess or ParseFailure)
3. `ServiceBusProcessorHost.ProcessMessageAsync` must not contain JSON parsing
4. `ServiceBusResponseWriter` must retry transient failures 3 times with exponential backoff
5. `ServiceBusConversationMapper` must provide `TryGetSourceId(chatId)` for reverse lookup
6. `IMessageSourceRouter` interface must be in Domain/Contracts
7. `CompositeChatMessengerClient` must use injected `IMessageSourceRouter`
8. All existing tests must continue to pass
9. New components must have unit tests

### Constraints

- Must not change `IChatMessengerClient` interface
- Must maintain backward compatibility with existing callers
- Must use existing Polly 8.6.5 package (already in Infrastructure.csproj)
- Must follow project patterns: primary constructors, file-scoped namespaces, record types

### Selected Approach

**Approach**: Component Extraction with Facade Pattern
**Description**: Extract focused components from existing monolithic classes while preserving the public API through facade wrappers. New components are injected via DI, existing public interfaces remain unchanged.
**Rationale**: Minimizes breaking changes while achieving single-responsibility. Test-first development ensures behavior preservation. Facade pattern allows incremental migration.
**Trade-offs Accepted**: Slightly more DI registrations (7 new services), temporary code duplication during migration

## Implementation Plan

### Domain/DTOs/ParsedServiceBusMessage.cs [create]

**Purpose**: DTO for successfully parsed Service Bus prompt messages
**TOTAL CHANGES**: 1

**Changes**:
1. Create new record type for parsed message data

**Implementation Details**:
- Record type with Prompt, Sender, SourceId, AgentId properties
- Immutable, no validation logic (parser handles validation)

**Reference Implementation**:
```csharp
namespace Domain.DTOs;

public sealed record ParsedServiceBusMessage(
    string Prompt,
    string Sender,
    string SourceId,
    string AgentId);
```

**Test File**: N/A (pure DTO, no logic to test)

**Success Criteria**:
- [ ] File compiles without errors
- [ ] Verification command: `dotnet build Domain/Domain.csproj`

**Dependencies**: None
**Provides**: `ParsedServiceBusMessage` record type

---

### Domain/DTOs/ParseResult.cs [create]

**Purpose**: Discriminated union for parse success/failure results
**TOTAL CHANGES**: 1

**Changes**:
1. Create abstract record base with sealed success/failure subtypes

**Implementation Details**:
- Abstract base `ParseResult`
- `ParseSuccess` with `ParsedServiceBusMessage Message`
- `ParseFailure` with `string Reason, string Details`

**Reference Implementation**:
```csharp
namespace Domain.DTOs;

public abstract record ParseResult;

public sealed record ParseSuccess(ParsedServiceBusMessage Message) : ParseResult;

public sealed record ParseFailure(string Reason, string Details) : ParseResult;
```

**Test File**: N/A (pure DTO, no logic to test)

**Success Criteria**:
- [ ] File compiles without errors
- [ ] Verification command: `dotnet build Domain/Domain.csproj`

**Dependencies**: `Domain/DTOs/ParsedServiceBusMessage.cs`
**Provides**: `ParseResult`, `ParseSuccess`, `ParseFailure` types

---

### Domain/Contracts/IMessageSourceRouter.cs [create]

**Purpose**: Contract for routing messages to appropriate clients based on source
**TOTAL CHANGES**: 1

**Changes**:
1. Create interface with `GetClientsForSource` method

**Implementation Details**:
- Interface in Domain layer for dependency inversion
- Takes client list and source, returns filtered clients

**Reference Implementation**:
```csharp
using Domain.DTOs;

namespace Domain.Contracts;

public interface IMessageSourceRouter
{
    IEnumerable<IChatMessengerClient> GetClientsForSource(
        IReadOnlyList<IChatMessengerClient> clients,
        MessageSource source);
}
```

**Test File**: N/A (interface, no logic)

**Success Criteria**:
- [ ] File compiles without errors
- [ ] Verification command: `dotnet build Domain/Domain.csproj`

**Dependencies**: None
**Provides**: `IMessageSourceRouter` interface

---

### Tests/Unit/Infrastructure/Messaging/ServiceBusMessageParserTests.cs [create]

**Purpose**: Unit tests for ServiceBusMessageParser (TDD - write before implementation)
**TOTAL CHANGES**: 1

**Changes**:
1. Create test class with tests for parse success, malformed JSON, missing prompt, sourceId/agentId extraction

**Implementation Details**:
- Mock `ServiceBusReceivedMessage` using test helpers
- Test ParseSuccess and ParseFailure paths
- Use Shouldly assertions

**Reference Implementation**:
```csharp
using Azure.Messaging.ServiceBus;
using Domain.DTOs;
using Infrastructure.Clients.Messaging;
using Shouldly;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusMessageParserTests
{
    private readonly ServiceBusMessageParser _parser = new("default-agent");

    [Fact]
    public void Parse_ValidMessage_ReturnsParseSuccess()
    {
        // Arrange
        var message = CreateMessage(
            body: """{"prompt": "Hello", "sender": "user1"}""",
            sourceId: "source-123",
            agentId: "agent-456");

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseSuccess>();
        var success = (ParseSuccess)result;
        success.Message.Prompt.ShouldBe("Hello");
        success.Message.Sender.ShouldBe("user1");
        success.Message.SourceId.ShouldBe("source-123");
        success.Message.AgentId.ShouldBe("agent-456");
    }

    [Fact]
    public void Parse_MissingPrompt_ReturnsParseFailure()
    {
        // Arrange
        var message = CreateMessage(
            body: """{"sender": "user1"}""",
            sourceId: "source-123");

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("MalformedMessage");
        failure.Details.ShouldContain("prompt");
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsParseFailure()
    {
        // Arrange
        var message = CreateMessage(body: "not json", sourceId: "source-123");

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("DeserializationError");
    }

    [Fact]
    public void Parse_MissingSourceId_GeneratesNewSourceId()
    {
        // Arrange
        var message = CreateMessage(
            body: """{"prompt": "Hello", "sender": "user1"}""",
            sourceId: null);

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseSuccess>();
        var success = (ParseSuccess)result;
        success.Message.SourceId.ShouldNotBeNullOrEmpty();
        success.Message.SourceId.Length.ShouldBe(32); // GUID without dashes
    }

    [Fact]
    public void Parse_MissingAgentId_UsesDefaultAgentId()
    {
        // Arrange
        var message = CreateMessage(
            body: """{"prompt": "Hello", "sender": "user1"}""",
            sourceId: "source-123",
            agentId: null);

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseSuccess>();
        var success = (ParseSuccess)result;
        success.Message.AgentId.ShouldBe("default-agent");
    }

    [Fact]
    public void Parse_EmptyPrompt_ReturnsParseFailure()
    {
        // Arrange
        var message = CreateMessage(
            body: """{"prompt": "", "sender": "user1"}""",
            sourceId: "source-123");

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("MalformedMessage");
    }

    private static ServiceBusReceivedMessage CreateMessage(string body, string? sourceId, string? agentId = null)
    {
        var props = new Dictionary<string, object>();
        if (sourceId is not null)
            props["sourceId"] = sourceId;
        if (agentId is not null)
            props["agentId"] = agentId;

        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(body),
            messageId: Guid.NewGuid().ToString(),
            applicationProperties: props);
    }
}
```

**Success Criteria**:
- [ ] Tests compile
- [ ] Tests fail (RED phase - parser not yet implemented)
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusMessageParserTests" --no-build || echo "Expected: tests fail before implementation"`

**Dependencies**: `Domain/DTOs/ParsedServiceBusMessage.cs`, `Domain/DTOs/ParseResult.cs`
**Provides**: Test coverage for `ServiceBusMessageParser`

---

### Infrastructure/Clients/Messaging/ServiceBusMessageParser.cs [create]

**Purpose**: Parse Service Bus messages to domain types with explicit success/failure results
**TOTAL CHANGES**: 1

**Changes**:
1. Create parser class with `Parse(ServiceBusReceivedMessage) -> ParseResult` method

**Implementation Details**:
- Primary constructor with `defaultAgentId`
- Try-catch for body read and JSON deserialization
- Return `ParseFailure` with reason codes for dead-lettering
- Extract sourceId/agentId from application properties with fallbacks

**Reference Implementation**:
```csharp
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Domain.DTOs;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusMessageParser(string defaultAgentId)
{
    public ParseResult Parse(ServiceBusReceivedMessage message)
    {
        string body;
        try
        {
            body = message.Body.ToString();
        }
        catch (Exception ex)
        {
            return new ParseFailure("BodyReadError", ex.Message);
        }

        ServiceBusPromptMessage? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ServiceBusPromptMessage>(body);
        }
        catch (JsonException ex)
        {
            return new ParseFailure("DeserializationError", ex.Message);
        }

        if (parsed is null || string.IsNullOrEmpty(parsed.Prompt))
        {
            return new ParseFailure("MalformedMessage", "Missing required 'prompt' field");
        }

        var sourceId = message.ApplicationProperties.TryGetValue("sourceId", out var sid)
            ? sid?.ToString() ?? GenerateSourceId()
            : GenerateSourceId();

        var agentId = message.ApplicationProperties.TryGetValue("agentId", out var aid)
            ? aid?.ToString() ?? defaultAgentId
            : defaultAgentId;

        return new ParseSuccess(new ParsedServiceBusMessage(
            parsed.Prompt,
            parsed.Sender,
            sourceId,
            agentId));
    }

    private static string GenerateSourceId() => Guid.NewGuid().ToString("N");
}
```

**Test File**: `Tests/Unit/Infrastructure/Messaging/ServiceBusMessageParserTests.cs`

**Success Criteria**:
- [ ] All ServiceBusMessageParserTests pass
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusMessageParserTests"`

**Dependencies**: `Domain/DTOs/ParsedServiceBusMessage.cs`, `Domain/DTOs/ParseResult.cs`
**Provides**: `ServiceBusMessageParser` class

---

### Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs [edit]

**Purpose**: Rename to ServiceBusConversationMapper and add reverse lookup capability
**TOTAL CHANGES**: 3

**Changes**:
1. Rename class from `ServiceBusSourceMapper` to `ServiceBusConversationMapper` (line 10)
2. Add `_chatIdToSourceId` ConcurrentDictionary field (after line 15)
3. Add `TryGetSourceId` method (after line 62)
4. Update `GetOrCreateMappingAsync` to populate reverse mapping (after line 33 for existing, line 55 for new)

**Implementation Details**:
- ConcurrentDictionary for thread-safe in-memory reverse lookup
- Populate on every mapping retrieval (both cache hit and miss)
- TryGetSourceId returns bool with out parameter pattern

**Migration Pattern**:
```csharp
// BEFORE (line 10):
public sealed class ServiceBusSourceMapper(

// AFTER:
public sealed class ServiceBusConversationMapper(
```

```csharp
// BEFORE (after line 15):
private readonly IDatabase _db = redis.GetDatabase();

// AFTER:
private readonly IDatabase _db = redis.GetDatabase();
private readonly ConcurrentDictionary<long, string> _chatIdToSourceId = new();
```

```csharp
// BEFORE (line 33, existing mapping):
return (existing.ChatId, existing.ThreadId, existing.TopicId, false);

// AFTER:
_chatIdToSourceId[existing.ChatId] = sourceId;
return (existing.ChatId, existing.ThreadId, existing.TopicId, false);
```

```csharp
// BEFORE (line 55, after StringSetAsync):
logger.LogInformation(

// AFTER:
_chatIdToSourceId[chatId] = sourceId;
logger.LogInformation(
```

**Reference Implementation** (full file after changes):
```csharp
using System.Collections.Concurrent;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusConversationMapper(
    IConnectionMultiplexer redis,
    IThreadStateStore threadStateStore,
    ILogger<ServiceBusConversationMapper> logger)
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly ConcurrentDictionary<long, string> _chatIdToSourceId = new();

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
                _chatIdToSourceId[existing.ChatId] = sourceId;
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
        await _db.StringSetAsync(redisKey, mappingJson, TimeSpan.FromDays(30), false);

        _chatIdToSourceId[chatId] = sourceId;

        logger.LogInformation(
            "Created new mapping for sourceId={SourceId}: chatId={ChatId}, threadId={ThreadId}, topicId={TopicId}",
            sourceId, chatId, threadId, topicId);

        return (chatId, threadId, topicId, true);
    }

    public bool TryGetSourceId(long chatId, out string sourceId)
        => _chatIdToSourceId.TryGetValue(chatId, out sourceId!);

    private sealed record SourceMapping(long ChatId, long ThreadId, string TopicId);
}
```

**Test File**: `Tests/Unit/Infrastructure/Messaging/ServiceBusSourceMapperTests.cs` - Update class references

**Success Criteria**:
- [ ] Existing ServiceBusSourceMapperTests pass after renaming references
- [ ] New TryGetSourceId method works correctly
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusSourceMapperTests"`

**Dependencies**: None
**Provides**: `ServiceBusConversationMapper` with `TryGetSourceId` method

---

### Tests/Unit/Infrastructure/Messaging/ServiceBusPromptReceiverTests.cs [create]

**Purpose**: Unit tests for ServiceBusPromptReceiver (TDD - write before implementation)
**TOTAL CHANGES**: 1

**Changes**:
1. Create test class with tests for enqueueing, channel reading, and sourceId lookup

**Reference Implementation**:
```csharp
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients.Messaging;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusPromptReceiverTests
{
    private readonly Mock<IConnectionMultiplexer> _redisMock;
    private readonly Mock<IDatabase> _dbMock;
    private readonly Mock<IThreadStateStore> _threadStateStoreMock;
    private readonly ServiceBusConversationMapper _mapper;
    private readonly ServiceBusPromptReceiver _receiver;

    public ServiceBusPromptReceiverTests()
    {
        _redisMock = new Mock<IConnectionMultiplexer>();
        _dbMock = new Mock<IDatabase>();
        _threadStateStoreMock = new Mock<IThreadStateStore>();
        var mapperLoggerMock = new Mock<ILogger<ServiceBusConversationMapper>>();
        var receiverLoggerMock = new Mock<ILogger<ServiceBusPromptReceiver>>();

        _redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_dbMock.Object);

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

        _threadStateStoreMock.Setup(s => s.SaveTopicAsync(It.IsAny<Domain.DTOs.WebChat.TopicMetadata>()))
            .Returns(Task.CompletedTask);

        _mapper = new ServiceBusConversationMapper(
            _redisMock.Object,
            _threadStateStoreMock.Object,
            mapperLoggerMock.Object);

        _receiver = new ServiceBusPromptReceiver(_mapper, receiverLoggerMock.Object);
    }

    [Fact]
    public async Task EnqueueAsync_ValidMessage_WritesToChannel()
    {
        // Arrange
        var message = new ParsedServiceBusMessage("Hello", "user1", "source-123", "agent-1");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act
        await _receiver.EnqueueAsync(message, cts.Token);

        // Assert - Read from channel to verify
        var prompts = new List<ChatPrompt>();
        await foreach (var prompt in _receiver.ReadPromptsAsync(cts.Token))
        {
            prompts.Add(prompt);
            break; // Just read one
        }

        prompts.Count.ShouldBe(1);
        prompts[0].Prompt.ShouldBe("Hello");
        prompts[0].Sender.ShouldBe("user1");
        prompts[0].AgentId.ShouldBe("agent-1");
        prompts[0].Source.ShouldBe(MessageSource.ServiceBus);
    }

    [Fact]
    public async Task TryGetSourceId_AfterEnqueue_ReturnsSourceId()
    {
        // Arrange
        var message = new ParsedServiceBusMessage("Hello", "user1", "source-123", "agent-1");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act
        await _receiver.EnqueueAsync(message, cts.Token);

        // Get chatId from channel
        ChatPrompt? prompt = null;
        await foreach (var p in _receiver.ReadPromptsAsync(cts.Token))
        {
            prompt = p;
            break;
        }

        // Assert
        prompt.ShouldNotBeNull();
        var found = _receiver.TryGetSourceId(prompt!.ChatId, out var sourceId);
        found.ShouldBeTrue();
        sourceId.ShouldBe("source-123");
    }

    [Fact]
    public async Task EnqueueAsync_MultipleMessages_IncrementsMessageId()
    {
        // Arrange
        var message1 = new ParsedServiceBusMessage("First", "user1", "source-1", "agent-1");
        var message2 = new ParsedServiceBusMessage("Second", "user1", "source-2", "agent-1");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act
        await _receiver.EnqueueAsync(message1, cts.Token);
        await _receiver.EnqueueAsync(message2, cts.Token);

        // Assert
        var prompts = new List<ChatPrompt>();
        await foreach (var p in _receiver.ReadPromptsAsync(cts.Token))
        {
            prompts.Add(p);
            if (prompts.Count >= 2) break;
        }

        prompts[0].MessageId.ShouldBeLessThan(prompts[1].MessageId);
    }
}
```

**Success Criteria**:
- [ ] Tests compile
- [ ] Tests fail (RED phase - receiver not yet implemented)
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusPromptReceiverTests" --no-build || echo "Expected: tests fail before implementation"`

**Dependencies**: `Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs` (renamed to ServiceBusConversationMapper)
**Provides**: Test coverage for `ServiceBusPromptReceiver`

---

### Infrastructure/Clients/Messaging/ServiceBusPromptReceiver.cs [create]

**Purpose**: Handle incoming prompts from Service Bus queue
**TOTAL CHANGES**: 1

**Changes**:
1. Create receiver class with channel, enqueueing, and sourceId lookup

**Implementation Details**:
- Channel<ChatPrompt> for async producer-consumer
- Enqueue method creates ChatPrompt from ParsedServiceBusMessage
- Delegates to ServiceBusConversationMapper for chatId/threadId mapping
- TryGetSourceId delegates to mapper for reverse lookup

**Reference Implementation**:
```csharp
using System.Threading.Channels;
using Domain.DTOs;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusPromptReceiver(
    ServiceBusConversationMapper conversationMapper,
    ILogger<ServiceBusPromptReceiver> logger)
{
    private readonly Channel<ChatPrompt> _channel = Channel.CreateUnbounded<ChatPrompt>();
    private int _messageIdCounter;

    public IAsyncEnumerable<ChatPrompt> ReadPromptsAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);

    public async Task EnqueueAsync(ParsedServiceBusMessage message, CancellationToken ct)
    {
        var (chatId, threadId, _, _) = await conversationMapper.GetOrCreateMappingAsync(
            message.SourceId, message.AgentId, ct);

        var prompt = new ChatPrompt
        {
            Prompt = message.Prompt,
            ChatId = chatId,
            ThreadId = (int)threadId,
            MessageId = Interlocked.Increment(ref _messageIdCounter),
            Sender = message.Sender,
            AgentId = message.AgentId,
            Source = MessageSource.ServiceBus
        };

        logger.LogInformation(
            "Enqueued prompt from Service Bus: sourceId={SourceId}, chatId={ChatId}",
            message.SourceId, chatId);

        await _channel.Writer.WriteAsync(prompt, ct);
    }

    public bool TryGetSourceId(long chatId, out string sourceId)
        => conversationMapper.TryGetSourceId(chatId, out sourceId);
}
```

**Test File**: `Tests/Unit/Infrastructure/Messaging/ServiceBusPromptReceiverTests.cs`

**Success Criteria**:
- [ ] All ServiceBusPromptReceiverTests pass
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusPromptReceiverTests"`

**Dependencies**: `Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs` (ServiceBusConversationMapper)
**Provides**: `ServiceBusPromptReceiver` class

---

### Tests/Unit/Infrastructure/Messaging/ServiceBusResponseWriterTests.cs [create]

**Purpose**: Unit tests for ServiceBusResponseWriter with Polly retry (TDD)
**TOTAL CHANGES**: 1

**Changes**:
1. Create test class with tests for successful send, retry on transient failure, and exhausted retries

**Reference Implementation**:
```csharp
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
```

**Success Criteria**:
- [ ] Tests compile
- [ ] Tests fail (RED phase - Polly retry not yet implemented)
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusResponseWriterTests" --no-build || echo "Expected: tests fail before implementation"`

**Dependencies**: None
**Provides**: Test coverage for `ServiceBusResponseWriter` retry logic

---

### Infrastructure/Clients/Messaging/ServiceBusResponseWriter.cs [edit]

**Purpose**: Add Polly retry with exponential backoff for transient failures
**TOTAL CHANGES**: 3

**Changes**:
1. Add Polly using statements (line 1)
2. Add ResiliencePipeline field and initialization in constructor (after line 9)
3. Wrap SendMessageAsync with retry pipeline (line 33)

**Migration Pattern**:
```csharp
// BEFORE (line 1-3):
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

// AFTER:
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
```

```csharp
// BEFORE (lines 7-10):
public class ServiceBusResponseWriter(
    ServiceBusSender sender,
    ILogger<ServiceBusResponseWriter> logger)
{

// AFTER:
public class ServiceBusResponseWriter
{
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusResponseWriter> _logger;
    private readonly ResiliencePipeline _retryPipeline;

    public ServiceBusResponseWriter(
        ServiceBusSender sender,
        ILogger<ServiceBusResponseWriter> logger)
    {
        _sender = sender;
        _logger = logger;
        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<ServiceBusException>(ex => ex.IsTransient)
                    .Handle<TimeoutException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception,
                        "Retry {Attempt}/3 for Service Bus send after {Delay}s",
                        args.AttemptNumber, args.RetryDelay.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }
```

```csharp
// BEFORE (line 33):
await sender.SendMessageAsync(message, ct);

// AFTER:
await _retryPipeline.ExecuteAsync(
    async token => await _sender.SendMessageAsync(message, token),
    ct);
```

**Reference Implementation** (full file after changes):
```csharp
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Infrastructure.Clients.Messaging;

public class ServiceBusResponseWriter
{
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusResponseWriter> _logger;
    private readonly ResiliencePipeline _retryPipeline;

    public ServiceBusResponseWriter(
        ServiceBusSender sender,
        ILogger<ServiceBusResponseWriter> logger)
    {
        _sender = sender;
        _logger = logger;
        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<ServiceBusException>(ex => ex.IsTransient)
                    .Handle<TimeoutException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception,
                        "Retry {Attempt}/3 for Service Bus send after {Delay}s",
                        args.AttemptNumber, args.RetryDelay.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

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

            await _retryPipeline.ExecuteAsync(
                async token => await _sender.SendMessageAsync(message, token),
                ct);

            _logger.LogDebug("Sent response to queue for sourceId={SourceId}", sourceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send response to queue after retries for sourceId={SourceId}", sourceId);
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

**Test File**: `Tests/Unit/Infrastructure/Messaging/ServiceBusResponseWriterTests.cs`

**Success Criteria**:
- [ ] All ServiceBusResponseWriterTests pass
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusResponseWriterTests"`

**Dependencies**: None
**Provides**: `ServiceBusResponseWriter` with Polly retry

---

### Tests/Unit/Infrastructure/Messaging/ServiceBusResponseHandlerTests.cs [create]

**Purpose**: Unit tests for ServiceBusResponseHandler (TDD)
**TOTAL CHANGES**: 1

**Changes**:
1. Create test class with tests for text accumulation and stream completion

**Reference Implementation**:
```csharp
using Domain.Agents;
using Domain.DTOs;
using Infrastructure.Clients.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

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
            .Returns((long _, out string s) => { s = "source-123"; return true; });

        var updates = new[]
        {
            (new AgentKey(chatId, 1, "agent-1"),
             new AgentResponseUpdate { Contents = [new TextContent("Hello ")] },
             (AiResponse?)null,
             MessageSource.ServiceBus),
            (new AgentKey(chatId, 1, "agent-1"),
             new AgentResponseUpdate { Contents = [new TextContent("World")] },
             (AiResponse?)null,
             MessageSource.ServiceBus),
            (new AgentKey(chatId, 1, "agent-1"),
             new AgentResponseUpdate { Contents = [new StreamCompleteContent()] },
             (AiResponse?)null,
             MessageSource.ServiceBus)
        }.ToAsyncEnumerable();

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

        var updates = new[]
        {
            (new AgentKey(999, 1, "agent-1"),
             new AgentResponseUpdate { Contents = [new TextContent("Hello")] },
             (AiResponse?)null,
             MessageSource.ServiceBus)
        }.ToAsyncEnumerable();

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
            .Returns((long _, out string s) => { s = "source-123"; return true; });

        var updates = new[]
        {
            (new AgentKey(chatId, 1, "agent-1"),
             new AgentResponseUpdate { Contents = [new StreamCompleteContent()] },
             (AiResponse?)null,
             MessageSource.ServiceBus)
        }.ToAsyncEnumerable();

        // Act
        await handler.ProcessAsync(updates, CancellationToken.None);

        // Assert
        writerMock.Verify(w => w.WriteResponseAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static (ServiceBusResponseHandler handler, Mock<ServiceBusPromptReceiver> receiverMock, Mock<ServiceBusResponseWriter> writerMock) CreateHandler()
    {
        var receiverMock = new Mock<ServiceBusPromptReceiver>(null!, null!);
        var writerMock = new Mock<ServiceBusResponseWriter>(null!, null!);
        var loggerMock = new Mock<ILogger<ServiceBusResponseHandler>>();

        var handler = new ServiceBusResponseHandler(
            receiverMock.Object,
            writerMock.Object,
            "default-agent",
            loggerMock.Object);

        return (handler, receiverMock, writerMock);
    }
}
```

**Success Criteria**:
- [ ] Tests compile
- [ ] Tests fail (RED phase - handler not yet implemented)
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusResponseHandlerTests" --no-build || echo "Expected: tests fail before implementation"`

**Dependencies**: `Infrastructure/Clients/Messaging/ServiceBusPromptReceiver.cs`, `Infrastructure/Clients/Messaging/ServiceBusResponseWriter.cs`
**Provides**: Test coverage for `ServiceBusResponseHandler`

---

### Infrastructure/Clients/Messaging/ServiceBusResponseHandler.cs [create]

**Purpose**: Accumulate response text and send completed responses
**TOTAL CHANGES**: 1

**Changes**:
1. Create handler class with text accumulation and stream completion handling

**Implementation Details**:
- ConcurrentDictionary for per-chat StringBuilders
- Process TextContent by appending to accumulator
- Process StreamCompleteContent by sending accumulated text and clearing
- Use ServiceBusPromptReceiver.TryGetSourceId for reverse lookup

**Reference Implementation**:
```csharp
using System.Collections.Concurrent;
using System.Text;
using Domain.Agents;
using Domain.DTOs;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusResponseHandler(
    ServiceBusPromptReceiver promptReceiver,
    ServiceBusResponseWriter responseWriter,
    string defaultAgentId,
    ILogger<ServiceBusResponseHandler> logger)
{
    private readonly ConcurrentDictionary<long, StringBuilder> _accumulators = new();

    public async Task ProcessAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
        CancellationToken ct)
    {
        await foreach (var (key, update, _, _) in updates.WithCancellation(ct))
        {
            if (!promptReceiver.TryGetSourceId(key.ChatId, out var sourceId))
                continue;

            await ProcessUpdateAsync(key, update, sourceId, ct);
        }
    }

    private async Task ProcessUpdateAsync(AgentKey key, AgentResponseUpdate update, string sourceId, CancellationToken ct)
    {
        var accumulator = _accumulators.GetOrAdd(key.ChatId, _ => new StringBuilder());

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                    accumulator.Append(tc.Text);
                    break;

                case StreamCompleteContent when accumulator.Length > 0:
                    await responseWriter.WriteResponseAsync(
                        sourceId, key.AgentId ?? defaultAgentId, accumulator.ToString(), ct);
                    accumulator.Clear();
                    _accumulators.TryRemove(key.ChatId, out _);
                    break;
            }
        }
    }
}
```

**Test File**: `Tests/Unit/Infrastructure/Messaging/ServiceBusResponseHandlerTests.cs`

**Success Criteria**:
- [ ] All ServiceBusResponseHandlerTests pass
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusResponseHandlerTests"`

**Dependencies**: `Infrastructure/Clients/Messaging/ServiceBusPromptReceiver.cs`, `Infrastructure/Clients/Messaging/ServiceBusResponseWriter.cs`
**Provides**: `ServiceBusResponseHandler` class

---

### Tests/Unit/Infrastructure/Messaging/MessageSourceRouterTests.cs [create]

**Purpose**: Unit tests for MessageSourceRouter (TDD)
**TOTAL CHANGES**: 1

**Changes**:
1. Create test class with tests for routing rules

**Reference Implementation**:
```csharp
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients.Messaging;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Messaging;

public class MessageSourceRouterTests
{
    private readonly MessageSourceRouter _router = new();

    [Fact]
    public void GetClientsForSource_WebUiSource_ReturnsOnlyWebUiClients()
    {
        // Arrange
        var webUiClient = CreateMockClient(MessageSource.WebUi);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus);
        var clients = new[] { webUiClient, serviceBusClient };

        // Act
        var result = _router.GetClientsForSource(clients, MessageSource.WebUi).ToList();

        // Assert
        result.Count.ShouldBe(1);
        result.ShouldContain(webUiClient);
    }

    [Fact]
    public void GetClientsForSource_ServiceBusSource_ReturnsWebUiAndServiceBusClients()
    {
        // Arrange
        var webUiClient = CreateMockClient(MessageSource.WebUi);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus);
        var telegramClient = CreateMockClient(MessageSource.Telegram);
        var clients = new[] { webUiClient, serviceBusClient, telegramClient };

        // Act
        var result = _router.GetClientsForSource(clients, MessageSource.ServiceBus).ToList();

        // Assert
        result.Count.ShouldBe(2);
        result.ShouldContain(webUiClient);
        result.ShouldContain(serviceBusClient);
        result.ShouldNotContain(telegramClient);
    }

    [Fact]
    public void GetClientsForSource_TelegramSource_ReturnsWebUiAndTelegramClients()
    {
        // Arrange
        var webUiClient = CreateMockClient(MessageSource.WebUi);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus);
        var telegramClient = CreateMockClient(MessageSource.Telegram);
        var clients = new[] { webUiClient, serviceBusClient, telegramClient };

        // Act
        var result = _router.GetClientsForSource(clients, MessageSource.Telegram).ToList();

        // Assert
        result.Count.ShouldBe(2);
        result.ShouldContain(webUiClient);
        result.ShouldContain(telegramClient);
        result.ShouldNotContain(serviceBusClient);
    }

    [Fact]
    public void GetClientsForSource_NoMatchingClients_ReturnsOnlyWebUi()
    {
        // Arrange
        var webUiClient = CreateMockClient(MessageSource.WebUi);
        var telegramClient = CreateMockClient(MessageSource.Telegram);
        var clients = new[] { webUiClient, telegramClient };

        // Act
        var result = _router.GetClientsForSource(clients, MessageSource.ServiceBus).ToList();

        // Assert
        result.Count.ShouldBe(1);
        result.ShouldContain(webUiClient);
    }

    private static IChatMessengerClient CreateMockClient(MessageSource source)
    {
        var mock = new Mock<IChatMessengerClient>();
        mock.Setup(c => c.Source).Returns(source);
        return mock.Object;
    }
}
```

**Success Criteria**:
- [ ] Tests compile
- [ ] Tests fail (RED phase - router not yet implemented)
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessageSourceRouterTests" --no-build || echo "Expected: tests fail before implementation"`

**Dependencies**: `Domain/Contracts/IMessageSourceRouter.cs`
**Provides**: Test coverage for `MessageSourceRouter`

---

### Infrastructure/Clients/Messaging/MessageSourceRouter.cs [create]

**Purpose**: Route messages to appropriate clients based on source
**TOTAL CHANGES**: 1

**Changes**:
1. Create router implementation with WebUI-receives-all + source-specific routing

**Reference Implementation**:
```csharp
using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.Clients.Messaging;

public sealed class MessageSourceRouter : IMessageSourceRouter
{
    public IEnumerable<IChatMessengerClient> GetClientsForSource(
        IReadOnlyList<IChatMessengerClient> clients,
        MessageSource source)
    {
        return clients.Where(c => c.Source == MessageSource.WebUi || c.Source == source);
    }
}
```

**Test File**: `Tests/Unit/Infrastructure/Messaging/MessageSourceRouterTests.cs`

**Success Criteria**:
- [ ] All MessageSourceRouterTests pass
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessageSourceRouterTests"`

**Dependencies**: `Domain/Contracts/IMessageSourceRouter.cs`
**Provides**: `MessageSourceRouter` class implementing `IMessageSourceRouter`

---

### Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs [edit]

**Purpose**: Convert to thin facade delegating to focused components
**TOTAL CHANGES**: 5

**Changes**:
1. Remove state fields (lines 20-23): `_promptChannel`, `_chatIdToSourceId`, `_responseAccumulators`, `_messageIdCounter`
2. Update constructor to inject `ServiceBusPromptReceiver` and `ServiceBusResponseHandler` instead of `ServiceBusSourceMapper` and `ServiceBusResponseWriter`
3. Replace `ReadPrompts` implementation (lines 28-36) to delegate to receiver
4. Replace `ProcessResponseStreamAsync` implementation (lines 38-76) to delegate to handler
5. Remove `EnqueueReceivedMessageAsync` method (lines 104-135) - moved to receiver

**Migration Pattern**:
```csharp
// BEFORE (lines 14-23):
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

// AFTER:
public sealed class ServiceBusChatMessengerClient(
    ServiceBusPromptReceiver promptReceiver,
    ServiceBusResponseHandler responseHandler,
    string defaultAgentId) : IChatMessengerClient
{
```

**Reference Implementation** (full file after changes):
```csharp
using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Agents.AI;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusChatMessengerClient(
    ServiceBusPromptReceiver promptReceiver,
    ServiceBusResponseHandler responseHandler,
    string defaultAgentId) : IChatMessengerClient
{
    public bool SupportsScheduledNotifications => false;
    public MessageSource Source => MessageSource.ServiceBus;

    public IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        => promptReceiver.ReadPromptsAsync(cancellationToken);

    public Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
        CancellationToken cancellationToken)
        => responseHandler.ProcessAsync(updates, cancellationToken);

    public Task<int> CreateThread(long chatId, string name, string? agentId, CancellationToken cancellationToken)
        => Task.FromResult(0);

    public Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId, CancellationToken cancellationToken)
        => Task.FromResult(false);

    public Task<AgentKey> CreateTopicIfNeededAsync(
        MessageSource source,
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default)
        => Task.FromResult(new AgentKey(chatId ?? 0, threadId ?? 0, agentId ?? defaultAgentId));

    public Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
        => Task.CompletedTask;
}
```

**Test File**: `Tests/Unit/Infrastructure/Messaging/ServiceBusChatMessengerClientTests.cs` - Update to use new constructor

**Success Criteria**:
- [ ] File is < 40 lines (excluding blank lines and using statements)
- [ ] Existing ServiceBusChatMessengerClientTests pass after updating constructor
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusChatMessengerClientTests"`

**Dependencies**: `Infrastructure/Clients/Messaging/ServiceBusPromptReceiver.cs`, `Infrastructure/Clients/Messaging/ServiceBusResponseHandler.cs`
**Provides**: Thin facade `ServiceBusChatMessengerClient` implementing `IChatMessengerClient`

---

### Infrastructure/Clients/Messaging/ServiceBusProcessorHost.cs [edit]

**Purpose**: Simplify to use parser and delegate to receiver
**TOTAL CHANGES**: 3

**Changes**:
1. Update constructor parameters: replace `ServiceBusChatMessengerClient` with `ServiceBusMessageParser` and `ServiceBusPromptReceiver` (line 9-12)
2. Replace inline parsing in `ProcessMessageAsync` with parser delegation (lines 30-70)
3. Use pattern matching on ParseResult for success/failure handling

**Migration Pattern**:
```csharp
// BEFORE (lines 9-12):
public sealed class ServiceBusProcessorHost(
    ServiceBusProcessor processor,
    ServiceBusChatMessengerClient messengerClient,
    ILogger<ServiceBusProcessorHost> logger) : BackgroundService

// AFTER:
public sealed class ServiceBusProcessorHost(
    ServiceBusProcessor processor,
    ServiceBusMessageParser parser,
    ServiceBusPromptReceiver promptReceiver,
    ILogger<ServiceBusProcessorHost> logger) : BackgroundService
```

**Reference Implementation** (full file after changes):
```csharp
using Azure.Messaging.ServiceBus;
using Domain.DTOs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusProcessorHost(
    ServiceBusProcessor processor,
    ServiceBusMessageParser parser,
    ServiceBusPromptReceiver promptReceiver,
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
            await processor.StopProcessingAsync(CancellationToken.None);
        }
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var result = parser.Parse(args.Message);

        switch (result)
        {
            case ParseSuccess success:
                await promptReceiver.EnqueueAsync(success.Message, args.CancellationToken);
                await args.CompleteMessageAsync(args.Message);
                break;

            case ParseFailure failure:
                logger.LogWarning("Failed to parse message: {Reason} - {Details}", failure.Reason, failure.Details);
                await args.DeadLetterMessageAsync(args.Message, failure.Reason, failure.Details);
                break;
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

**Test File**: N/A (ProcessorHost tested via integration tests)

**Success Criteria**:
- [ ] No JSON parsing code in ProcessMessageAsync
- [ ] ProcessMessageAsync uses pattern matching on ParseResult
- [ ] Verification command: `dotnet build Infrastructure/Infrastructure.csproj`

**Dependencies**: `Infrastructure/Clients/Messaging/ServiceBusMessageParser.cs`, `Infrastructure/Clients/Messaging/ServiceBusPromptReceiver.cs`
**Provides**: Simplified `ServiceBusProcessorHost`

---

### Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs [edit]

**Purpose**: Use injected IMessageSourceRouter instead of private method
**TOTAL CHANGES**: 3

**Changes**:
1. Add `IMessageSourceRouter` parameter to constructor (line 10-11)
2. Replace `GetClientsForSource` calls with `router.GetClientsForSource` (lines 69, 81, 110)
3. Remove private `GetClientsForSource` method (lines 94-97)

**Migration Pattern**:
```csharp
// BEFORE (lines 10-11):
public sealed class CompositeChatMessengerClient(
    IReadOnlyList<IChatMessengerClient> clients) : IChatMessengerClient

// AFTER:
public sealed class CompositeChatMessengerClient(
    IReadOnlyList<IChatMessengerClient> clients,
    IMessageSourceRouter router) : IChatMessengerClient
```

```csharp
// BEFORE (line 69):
var tasks = GetClientsForSource(source)

// AFTER:
var tasks = router.GetClientsForSource(clients, source)
```

**Reference Implementation** (full file after changes):
```csharp
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Agents.AI;

namespace Infrastructure.Clients.Messaging;

public sealed class CompositeChatMessengerClient(
    IReadOnlyList<IChatMessengerClient> clients,
    IMessageSourceRouter router) : IChatMessengerClient
{
    public MessageSource Source => MessageSource.WebUi;

    public bool SupportsScheduledNotifications => clients.Any(c => c.SupportsScheduledNotifications);

    public IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken)
    {
        Validate();
        return clients
            .Select(c => c.ReadPrompts(timeout, cancellationToken))
            .Merge(cancellationToken);
    }

    public async Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
        CancellationToken cancellationToken)
    {
        Validate();
        var channels = clients
            .Select(_ => Channel.CreateUnbounded<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>())
            .ToArray();

        var clientChannelPairs = clients
            .Zip(channels, (client, channel) => (client, channel))
            .ToArray();

        var broadcastTask = BroadcastUpdatesAsync(updates, clientChannelPairs, cancellationToken);

        var processTasks = clientChannelPairs
            .Select(pair => pair.client.ProcessResponseStreamAsync(
                pair.channel.Reader.ReadAllAsync(cancellationToken),
                cancellationToken))
            .ToArray();

        await broadcastTask;
        await Task.WhenAll(processTasks);
    }

    public async Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId,
        CancellationToken cancellationToken)
    {
        Validate();
        var existsTasks = clients
            .Select(client => client.DoesThreadExist(chatId, threadId, agentId, cancellationToken));
        var results = await Task.WhenAll(existsTasks);
        return results.Any(exists => exists);
    }

    public async Task<AgentKey> CreateTopicIfNeededAsync(
        MessageSource source,
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default)
    {
        Validate();
        var tasks = router.GetClientsForSource(clients, source)
            .Select(c => c.CreateTopicIfNeededAsync(source, chatId, threadId, agentId, topicName, ct));
        var results = await Task.WhenAll(tasks);

        return results
            .DefaultIfEmpty(new AgentKey(chatId ?? 0, threadId ?? 0, agentId ?? string.Empty))
            .First();
    }

    public async Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
    {
        Validate();
        var tasks = router.GetClientsForSource(clients, source)
            .Select(c => c.StartScheduledStreamAsync(agentKey, source, ct));
        await Task.WhenAll(tasks);
    }

    private void Validate()
    {
        if (clients.Count == 0)
        {
            throw new InvalidOperationException($"{nameof(clients)} must contain at least one client");
        }
    }

    private async Task BroadcastUpdatesAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> source,
        (IChatMessengerClient client, Channel<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> channel)[]
            clientChannelPairs,
        CancellationToken ct)
    {
        try
        {
            await foreach (var update in source.WithCancellation(ct))
            {
                var (_, _, _, messageSource) = update;
                var targetClients = router.GetClientsForSource(clients, messageSource).ToHashSet();

                var writeTasks = clientChannelPairs
                    .Where(pair => targetClients.Contains(pair.client))
                    .Select(pair => pair.channel.Writer.WriteAsync(update, ct).AsTask());

                await Task.WhenAll(writeTasks);
            }
        }
        finally
        {
            foreach (var (_, channel) in clientChannelPairs)
            {
                channel.Writer.TryComplete();
            }
        }
    }
}
```

**Test File**: `Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs` - Update to inject router

**Success Criteria**:
- [ ] No private `GetClientsForSource` method
- [ ] All CompositeChatMessengerClientTests pass after injecting router
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~CompositeChatMessengerClientTests"`

**Dependencies**: `Domain/Contracts/IMessageSourceRouter.cs`, `Infrastructure/Clients/Messaging/MessageSourceRouter.cs`
**Provides**: Updated `CompositeChatMessengerClient` using injected router

---

### Agent/Modules/InjectorModule.cs [edit]

**Purpose**: Update DI registration for new component structure
**TOTAL CHANGES**: 4

**Changes**:
1. Update `AddServiceBusClient` method to register new components (lines 172-204)
2. Register `ServiceBusConversationMapper` instead of `ServiceBusSourceMapper` (line 190)
3. Register `ServiceBusMessageParser`, `ServiceBusPromptReceiver`, `ServiceBusResponseHandler`
4. Register `MessageSourceRouter` as `IMessageSourceRouter`
5. Update `ServiceBusChatMessengerClient` and `ServiceBusProcessorHost` registrations

**Migration Pattern**:
```csharp
// BEFORE (lines 190-203):
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

// AFTER:
.AddSingleton<ServiceBusConversationMapper>()
.AddSingleton(sp => new ServiceBusMessageParser(defaultAgentId))
.AddSingleton(sp => new ServiceBusPromptReceiver(
    sp.GetRequiredService<ServiceBusConversationMapper>(),
    sp.GetRequiredService<ILogger<ServiceBusPromptReceiver>>()))
.AddSingleton(sp => new ServiceBusResponseWriter(
    sp.GetRequiredService<ServiceBusSender>(),
    sp.GetRequiredService<ILogger<ServiceBusResponseWriter>>()))
.AddSingleton(sp => new ServiceBusResponseHandler(
    sp.GetRequiredService<ServiceBusPromptReceiver>(),
    sp.GetRequiredService<ServiceBusResponseWriter>(),
    defaultAgentId,
    sp.GetRequiredService<ILogger<ServiceBusResponseHandler>>()))
.AddSingleton(sp => new ServiceBusChatMessengerClient(
    sp.GetRequiredService<ServiceBusPromptReceiver>(),
    sp.GetRequiredService<ServiceBusResponseHandler>(),
    defaultAgentId))
.AddSingleton<IMessageSourceRouter, MessageSourceRouter>()
.AddSingleton<IChatMessengerClient>(sp => new CompositeChatMessengerClient(
    [
        sp.GetRequiredService<WebChatMessengerClient>(),
        sp.GetRequiredService<ServiceBusChatMessengerClient>()
    ],
    sp.GetRequiredService<IMessageSourceRouter>()))
.AddSingleton(sp => new ServiceBusProcessorHost(
    sp.GetRequiredService<ServiceBusProcessor>(),
    sp.GetRequiredService<ServiceBusMessageParser>(),
    sp.GetRequiredService<ServiceBusPromptReceiver>(),
    sp.GetRequiredService<ILogger<ServiceBusProcessorHost>>()))
.AddHostedService(sp => sp.GetRequiredService<ServiceBusProcessorHost>());
```

**Reference Implementation** (AddServiceBusClient method after changes):
```csharp
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
        .AddSingleton<ServiceBusConversationMapper>()
        .AddSingleton(sp => new ServiceBusMessageParser(defaultAgentId))
        .AddSingleton(sp => new ServiceBusPromptReceiver(
            sp.GetRequiredService<ServiceBusConversationMapper>(),
            sp.GetRequiredService<ILogger<ServiceBusPromptReceiver>>()))
        .AddSingleton(sp => new ServiceBusResponseWriter(
            sp.GetRequiredService<ServiceBusSender>(),
            sp.GetRequiredService<ILogger<ServiceBusResponseWriter>>()))
        .AddSingleton(sp => new ServiceBusResponseHandler(
            sp.GetRequiredService<ServiceBusPromptReceiver>(),
            sp.GetRequiredService<ServiceBusResponseWriter>(),
            defaultAgentId,
            sp.GetRequiredService<ILogger<ServiceBusResponseHandler>>()))
        .AddSingleton(sp => new ServiceBusChatMessengerClient(
            sp.GetRequiredService<ServiceBusPromptReceiver>(),
            sp.GetRequiredService<ServiceBusResponseHandler>(),
            defaultAgentId))
        .AddSingleton<IMessageSourceRouter, MessageSourceRouter>()
        .AddSingleton<IChatMessengerClient>(sp => new CompositeChatMessengerClient(
            [
                sp.GetRequiredService<WebChatMessengerClient>(),
                sp.GetRequiredService<ServiceBusChatMessengerClient>()
            ],
            sp.GetRequiredService<IMessageSourceRouter>()))
        .AddSingleton(sp => new ServiceBusProcessorHost(
            sp.GetRequiredService<ServiceBusProcessor>(),
            sp.GetRequiredService<ServiceBusMessageParser>(),
            sp.GetRequiredService<ServiceBusPromptReceiver>(),
            sp.GetRequiredService<ILogger<ServiceBusProcessorHost>>()))
        .AddHostedService(sp => sp.GetRequiredService<ServiceBusProcessorHost>());
}
```

**Test File**: `Tests/Integration/Jack/DependencyInjectionTests.cs` - Verify DI registration

**Success Criteria**:
- [ ] Application starts without DI errors
- [ ] All new services are resolvable
- [ ] Verification command: `dotnet build Agent/Agent.csproj && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~DependencyInjectionTests"`

**Dependencies**: All new components must be created first
**Provides**: Updated DI registration for all components

---

### Tests/Unit/Infrastructure/Messaging/ServiceBusSourceMapperTests.cs [edit]

**Purpose**: Update test class to use renamed ServiceBusConversationMapper
**TOTAL CHANGES**: 2

**Changes**:
1. Rename all `ServiceBusSourceMapper` references to `ServiceBusConversationMapper`
2. Add test for `TryGetSourceId` method

**Migration Pattern**:
```csharp
// BEFORE (line 15):
private readonly ServiceBusSourceMapper _mapper;

// AFTER:
private readonly ServiceBusConversationMapper _mapper;
```

**Reference Implementation** (add test for TryGetSourceId):
```csharp
[Fact]
public async Task TryGetSourceId_AfterMapping_ReturnsSourceId()
{
    // Arrange
    const string sourceId = "test-source";
    const string agentId = "default";

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
    var (chatId, _, _, _) = await _mapper.GetOrCreateMappingAsync(sourceId, agentId);
    var found = _mapper.TryGetSourceId(chatId, out var retrievedSourceId);

    // Assert
    found.ShouldBeTrue();
    retrievedSourceId.ShouldBe(sourceId);
}

[Fact]
public void TryGetSourceId_UnknownChatId_ReturnsFalse()
{
    // Act
    var found = _mapper.TryGetSourceId(999999, out var sourceId);

    // Assert
    found.ShouldBeFalse();
}
```

**Success Criteria**:
- [ ] All tests pass with renamed class
- [ ] New TryGetSourceId tests pass
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusSourceMapperTests"`

**Dependencies**: `Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs` (renamed)
**Provides**: Updated test coverage for `ServiceBusConversationMapper`

---

### Tests/Unit/Infrastructure/Messaging/ServiceBusChatMessengerClientTests.cs [edit]

**Purpose**: Update test class to use new constructor with injected components
**TOTAL CHANGES**: 2

**Changes**:
1. Update constructor setup to create mock ServiceBusPromptReceiver and ServiceBusResponseHandler
2. Adjust test expectations for facade behavior

**Migration Pattern**:
```csharp
// BEFORE (constructor setup):
var mapper = new ServiceBusSourceMapper(...);
var writerMock = new Mock<ServiceBusResponseWriter>(...);
_client = new ServiceBusChatMessengerClient(
    mapper,
    writerMock.Object,
    clientLoggerMock.Object,
    "default");

// AFTER:
var receiverMock = new Mock<ServiceBusPromptReceiver>(null!, null!);
var handlerMock = new Mock<ServiceBusResponseHandler>(null!, null!, null!, null!);
_client = new ServiceBusChatMessengerClient(
    receiverMock.Object,
    handlerMock.Object,
    "default");
```

**Reference Implementation** (updated test class):
```csharp
using Domain.Agents;
using Domain.DTOs;
using Infrastructure.Clients.Messaging;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusChatMessengerClientTests
{
    private readonly ServiceBusChatMessengerClient _client;

    public ServiceBusChatMessengerClientTests()
    {
        var receiverMock = new Mock<ServiceBusPromptReceiver>(null!, null!);
        var handlerMock = new Mock<ServiceBusResponseHandler>(null!, null!, null!, null!);

        _client = new ServiceBusChatMessengerClient(
            receiverMock.Object,
            handlerMock.Object,
            "default");
    }

    [Fact]
    public void SupportsScheduledNotifications_ReturnsFalse()
    {
        _client.SupportsScheduledNotifications.ShouldBeFalse();
    }

    [Fact]
    public void Source_ReturnsServiceBus()
    {
        _client.Source.ShouldBe(MessageSource.ServiceBus);
    }

    [Fact]
    public async Task CreateTopicIfNeededAsync_WithExistingChatAndThread_ReturnsAgentKey()
    {
        // Act
        var result = await _client.CreateTopicIfNeededAsync(MessageSource.ServiceBus, 123, 456, "agent1", "test topic");

        // Assert
        result.ChatId.ShouldBe(123);
        result.ThreadId.ShouldBe(456);
        result.AgentId.ShouldBe("agent1");
    }

    [Fact]
    public async Task CreateTopicIfNeededAsync_WithNullAgentId_UsesDefaultAgentId()
    {
        // Act
        var result = await _client.CreateTopicIfNeededAsync(MessageSource.ServiceBus, 123, 456, null, "test topic");

        // Assert
        result.AgentId.ShouldBe("default");
    }

    [Fact]
    public async Task CreateThread_ReturnsZero()
    {
        // Act
        var result = await _client.CreateThread(123, "test", "agent1", CancellationToken.None);

        // Assert
        result.ShouldBe(0);
    }

    [Fact]
    public async Task DoesThreadExist_ReturnsFalse()
    {
        // Act
        var result = await _client.DoesThreadExist(123, 456, "agent1", CancellationToken.None);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task StartScheduledStreamAsync_CompletesWithoutError()
    {
        // Act & Assert - should not throw
        await Should.NotThrowAsync(async () =>
            await _client.StartScheduledStreamAsync(new AgentKey(1, 1, "agent1"), MessageSource.ServiceBus));
    }
}
```

**Success Criteria**:
- [ ] All tests pass with new constructor
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusChatMessengerClientTests"`

**Dependencies**: `Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs`
**Provides**: Updated test coverage for facade

---

### Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs [edit]

**Purpose**: Update test class to inject IMessageSourceRouter
**TOTAL CHANGES**: 1

**Changes**:
1. Update all `CompositeChatMessengerClient` instantiations to include `MessageSourceRouter`

**Migration Pattern**:
```csharp
// BEFORE:
var composite = new CompositeChatMessengerClient([client1.Object, client2.Object]);

// AFTER:
var router = new MessageSourceRouter();
var composite = new CompositeChatMessengerClient([client1.Object, client2.Object], router);
```

**Success Criteria**:
- [ ] All tests pass with router injection
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~CompositeChatMessengerClientTests"`

**Dependencies**: `Infrastructure/Clients/Messaging/MessageSourceRouter.cs`, `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs`
**Provides**: Updated test coverage for `CompositeChatMessengerClient`

## Dependency Graph

> Files in the same phase can execute in parallel.

| Phase | File | Action | Depends On |
|-------|------|--------|------------|
| 1 | `Domain/DTOs/ParsedServiceBusMessage.cs` | create | - |
| 1 | `Domain/Contracts/IMessageSourceRouter.cs` | create | - |
| 2 | `Domain/DTOs/ParseResult.cs` | create | `Domain/DTOs/ParsedServiceBusMessage.cs` |
| 3 | `Tests/Unit/Infrastructure/Messaging/ServiceBusMessageParserTests.cs` | create | `Domain/DTOs/ParseResult.cs` |
| 3 | `Tests/Unit/Infrastructure/Messaging/MessageSourceRouterTests.cs` | create | `Domain/Contracts/IMessageSourceRouter.cs` |
| 3 | `Tests/Unit/Infrastructure/Messaging/ServiceBusResponseWriterTests.cs` | create | - |
| 4 | `Infrastructure/Clients/Messaging/ServiceBusMessageParser.cs` | create | `Tests/Unit/Infrastructure/Messaging/ServiceBusMessageParserTests.cs` |
| 4 | `Infrastructure/Clients/Messaging/MessageSourceRouter.cs` | create | `Tests/Unit/Infrastructure/Messaging/MessageSourceRouterTests.cs` |
| 4 | `Infrastructure/Clients/Messaging/ServiceBusResponseWriter.cs` | edit | `Tests/Unit/Infrastructure/Messaging/ServiceBusResponseWriterTests.cs` |
| 4 | `Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs` | edit | - |
| 5 | `Tests/Unit/Infrastructure/Messaging/ServiceBusSourceMapperTests.cs` | edit | `Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs` |
| 5 | `Tests/Unit/Infrastructure/Messaging/ServiceBusPromptReceiverTests.cs` | create | `Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs` |
| 6 | `Infrastructure/Clients/Messaging/ServiceBusPromptReceiver.cs` | create | `Tests/Unit/Infrastructure/Messaging/ServiceBusPromptReceiverTests.cs`, `Infrastructure/Clients/Messaging/ServiceBusSourceMapper.cs` |
| 7 | `Tests/Unit/Infrastructure/Messaging/ServiceBusResponseHandlerTests.cs` | create | `Infrastructure/Clients/Messaging/ServiceBusPromptReceiver.cs`, `Infrastructure/Clients/Messaging/ServiceBusResponseWriter.cs` |
| 8 | `Infrastructure/Clients/Messaging/ServiceBusResponseHandler.cs` | create | `Tests/Unit/Infrastructure/Messaging/ServiceBusResponseHandlerTests.cs` |
| 9 | `Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs` | edit | `Infrastructure/Clients/Messaging/ServiceBusPromptReceiver.cs`, `Infrastructure/Clients/Messaging/ServiceBusResponseHandler.cs` |
| 9 | `Infrastructure/Clients/Messaging/ServiceBusProcessorHost.cs` | edit | `Infrastructure/Clients/Messaging/ServiceBusMessageParser.cs`, `Infrastructure/Clients/Messaging/ServiceBusPromptReceiver.cs` |
| 9 | `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs` | edit | `Infrastructure/Clients/Messaging/MessageSourceRouter.cs` |
| 10 | `Tests/Unit/Infrastructure/Messaging/ServiceBusChatMessengerClientTests.cs` | edit | `Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs` |
| 10 | `Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs` | edit | `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs` |
| 11 | `Agent/Modules/InjectorModule.cs` | edit | All Infrastructure components |

## Exit Criteria

### Test Commands
```bash
dotnet build                                    # Verify compilation
dotnet test Tests/Tests.csproj                  # Run all tests
```

### Success Conditions
- [ ] All tests pass (exit code 0)
- [ ] No compilation errors
- [ ] `ServiceBusChatMessengerClient` is < 40 lines (facade pattern)
- [ ] `ServiceBusProcessorHost.ProcessMessageAsync` has no JSON parsing
- [ ] `ServiceBusResponseWriter` has Polly retry with 3 attempts
- [ ] `ServiceBusConversationMapper` has `TryGetSourceId` method
- [ ] `CompositeChatMessengerClient` uses injected `IMessageSourceRouter`
- [ ] All 9 requirements satisfied
- [ ] LSP verification passes (no dead code, unused symbols)

### Verification Script
```bash
dotnet build && dotnet test Tests/Tests.csproj
```

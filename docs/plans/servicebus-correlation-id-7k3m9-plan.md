# Service Bus Explicit Correlation ID and Agent ID

## Summary

Make `correlationId` and `agentId` **required fields** in Service Bus prompt messages by moving them from application properties to the JSON body. The `correlationId` replaces `sourceId` as both the request/response correlation identifier and the conversation thread identifier for Redis persistence. This requires updating DTOs, parser validation, response writer, conversation mapper, DI registration, and all related tests.

## Files

> **Note**: This is the canonical file list.

### Files to Edit
- `Domain/DTOs/ServiceBusPromptMessage.cs`
- `Domain/DTOs/ParsedServiceBusMessage.cs`
- `Domain/DTOs/ParseResult.cs`
- `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusMessageParser.cs`
- `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusResponseWriter.cs`
- `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusSourceMapper.cs`
- `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusPromptReceiver.cs`
- `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusResponseHandler.cs`
- `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusChatMessengerClient.cs`
- `Agent/Modules/InjectorModule.cs`
- `Tests/Unit/Infrastructure/Messaging/ServiceBusMessageParserTests.cs`
- `Tests/Unit/Infrastructure/Messaging/ServiceBusSourceMapperTests.cs`
- `Tests/Unit/Infrastructure/Messaging/ServiceBusPromptReceiverTests.cs`
- `Tests/Unit/Infrastructure/Messaging/ServiceBusResponseWriterTests.cs`
- `Tests/Unit/Infrastructure/Messaging/ServiceBusResponseHandlerTests.cs`
- `Tests/Integration/Messaging/ServiceBusIntegrationTests.cs`
- `Tests/Integration/Fixtures/ServiceBusFixture.cs`

### Files to Create
- `Domain/DTOs/ServiceBusResponseMessage.cs`

## Code Context

### Current Implementation

**ServiceBusPromptMessage.cs (Domain/DTOs/ServiceBusPromptMessage.cs:1-15)**:
```csharp
public record ServiceBusPromptMessage
{
    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [JsonPropertyName("sender")]
    public required string Sender { get; init; }
}
```
Currently only has `prompt` and `sender` fields. Design requires adding `correlationId` and `agentId`.

**ParsedServiceBusMessage.cs (Domain/DTOs/ParsedServiceBusMessage.cs:1-8)**:
```csharp
public sealed record ParsedServiceBusMessage(
    string Prompt,
    string Sender,
    string SourceId,
    string AgentId);
```
Uses `SourceId` which needs to be renamed to `CorrelationId`.

**ServiceBusMessageParser.cs (Infrastructure/Clients/Messaging/ServiceBus/ServiceBusMessageParser.cs:1-62)**:
- Constructor takes `string defaultAgentId`
- Reads `sourceId` and `agentId` from `message.ApplicationProperties` (lines 43-49)
- Generates `sourceId` if missing (line 58-61)
- Uses `defaultAgentId` as fallback for `agentId`

**ServiceBusResponseWriter.cs (Infrastructure/Clients/Messaging/ServiceBus/ServiceBusResponseWriter.cs:1-82)**:
- Method `WriteResponseAsync` takes `sourceId` parameter (line 41-46)
- Uses private `ServiceBusResponseMessage` record with `SourceId` (lines 75-81)

**ServiceBusConversationMapper.cs (Infrastructure/Clients/Messaging/ServiceBus/ServiceBusSourceMapper.cs:1-75)**:
- Redis key pattern: `sb-source:{agentId}:{sourceId}` (line 24)
- Dictionary: `_chatIdToSourceId` (line 17)
- Method: `TryGetSourceId` (line 69-72)

**ServiceBusPromptReceiver.cs (Infrastructure/Clients/Messaging/ServiceBus/ServiceBusPromptReceiver.cs:1-46)**:
- Uses `message.SourceId` (line 22)
- Method `TryGetSourceId` delegates to mapper (line 42-45)

**ServiceBusResponseHandler.cs (Infrastructure/Clients/Messaging/ServiceBus/ServiceBusResponseHandler.cs:1-35)**:
- Constructor takes `string defaultAgentId` (line 10)
- Uses `GetSourceId` internally (line 18, 29-34)

**ServiceBusChatMessengerClient.cs (Infrastructure/Clients/Messaging/ServiceBus/ServiceBusChatMessengerClient.cs:1-48)**:
- Constructor takes `string defaultAgentId` (line 11)
- Uses `defaultAgentId` in `CreateTopicIfNeededAsync` (line 41)

**InjectorModule.cs (Agent/Modules/InjectorModule.cs:177-224)**:
- `AddServiceBusClient` takes `string defaultAgentId` (line 177)
- Creates `ServiceBusMessageParser(defaultAgentId)` (line 196)
- Creates `ServiceBusResponseHandler(..., defaultAgentId)` (lines 203-206)
- Creates `ServiceBusChatMessengerClient(..., defaultAgentId)` (lines 207-210)

### Patterns to Follow

**Record with JSON attributes (Domain/DTOs/ServiceBusPromptMessage.cs)**:
```csharp
public record ServiceBusPromptMessage
{
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }
    ...
}
```

**Primary constructors for DI (per CONVENTIONS.md)**:
```csharp
public sealed class ServiceBusMessageParser(IReadOnlyList<string> validAgentIds)
```

**Shouldly assertions in tests (per TESTING.md)**:
```csharp
result.ShouldBeOfType<ParseFailure>();
failure.Reason.ShouldBe("MissingField");
```

## External Context

N/A - This is an internal refactoring of existing Service Bus integration. No external libraries or APIs are being added.

## Architectural Narrative

### Task

Make `correlationId` and `agentId` required fields in Service Bus prompt messages by:
1. Moving these fields from application properties to the JSON message body
2. Renaming `sourceId` to `correlationId` throughout the codebase
3. Validating `agentId` against configured agents (strict validation)
4. Removing the `defaultAgentId` concept from Service Bus components
5. Dead-lettering messages with missing required fields or invalid agent IDs

### Architecture

The Service Bus integration follows this flow:
```
ServiceBusProcessorHost (BackgroundService)
    ↓ ProcessMessageAsync
ServiceBusMessageParser.Parse()
    ↓ ParseSuccess/ParseFailure
ServiceBusPromptReceiver.EnqueueAsync()
    ↓ uses ServiceBusConversationMapper
Channel<ChatPrompt>
    ↓ ReadPromptsAsync()
ChatMonitor
    ↓ processes with agent
ServiceBusResponseHandler.ProcessAsync()
    ↓ uses ServiceBusResponseWriter
Response queue
```

Key files and their roles:
- `ServiceBusMessageParser`: Validates incoming messages, extracts fields from JSON body
- `ServiceBusConversationMapper`: Maps correlationId+agentId to chatId/threadId, persists in Redis
- `ServiceBusPromptReceiver`: Enqueues parsed messages for processing
- `ServiceBusResponseHandler`: Writes responses back to response queue
- `ServiceBusResponseWriter`: Serializes and sends response messages

### Selected Context

| File | Provides |
|------|----------|
| `Domain/DTOs/ServiceBusPromptMessage.cs` | Input message DTO with JSON deserialization |
| `Domain/DTOs/ParsedServiceBusMessage.cs` | Validated message passed between components |
| `Domain/DTOs/ParseResult.cs` | Parse result discriminated union |
| `Infrastructure/.../ServiceBusMessageParser.cs` | Message validation and parsing |
| `Infrastructure/.../ServiceBusSourceMapper.cs` | CorrelationId to ChatId mapping |
| `Infrastructure/.../ServiceBusResponseWriter.cs` | Response message serialization |
| `Agent/Modules/InjectorModule.cs` | DI registration for Service Bus components |

### Relationships

```
InjectorModule
    ├── creates ServiceBusMessageParser(validAgentIds)
    ├── creates ServiceBusConversationMapper
    ├── creates ServiceBusPromptReceiver(mapper)
    ├── creates ServiceBusResponseWriter
    ├── creates ServiceBusResponseHandler(receiver, writer)
    └── creates ServiceBusChatMessengerClient(receiver, handler)

ServiceBusProcessorHost
    └── uses ServiceBusMessageParser.Parse()
         └── returns ParseSuccess(ParsedServiceBusMessage) or ParseFailure

ServiceBusPromptReceiver
    └── uses ServiceBusConversationMapper.GetOrCreateMappingAsync()

ServiceBusResponseHandler
    └── uses ServiceBusPromptReceiver.TryGetCorrelationId()
    └── uses ServiceBusResponseWriter.WriteResponseAsync()
```

### External Context

N/A - Internal refactoring only.

### Implementation Notes

1. **Validation order in parser**:
   - Deserialize JSON body
   - Check `correlationId` is present and non-empty
   - Check `agentId` is present and non-empty
   - Check `agentId` exists in `validAgentIds`
   - Check `prompt` is present and non-empty
   - Check `sender` is present

2. **Dead-letter reasons**:
   - `MissingField` - When correlationId, agentId, prompt, or sender is missing
   - `InvalidAgentId` - When agentId does not match any configured agent

3. **Redis key migration**: The Redis key pattern changes from `sb-source:{agentId}:{sourceId}` to `sb-correlation:{agentId}:{correlationId}`. Existing conversations will not be found with new pattern (acceptable for this change).

4. **Response message format**: The public `ServiceBusResponseMessage` DTO uses `correlationId` instead of `sourceId`.

### Ambiguities

1. **Decision made**: Existing conversations with old Redis keys will not be migrated. New conversations will use the new key pattern.

2. **Decision made**: The `sender` field remains optional in validation since it has a reasonable default in the existing codebase (empty string is acceptable).

### Requirements

1. `correlationId` MUST be a required field in the JSON message body
2. `agentId` MUST be a required field in the JSON message body
3. `agentId` MUST be validated against configured agent IDs
4. Messages with missing required fields MUST be dead-lettered with reason `MissingField`
5. Messages with invalid `agentId` MUST be dead-lettered with reason `InvalidAgentId`
6. Response messages MUST include `correlationId` (renamed from `sourceId`)
7. Redis keys MUST use pattern `sb-correlation:{agentId}:{correlationId}`
8. All `sourceId` references MUST be renamed to `correlationId`
9. `defaultAgentId` MUST be removed from all Service Bus components
10. All existing tests MUST be updated to use new message format

### Constraints

- Domain layer MUST NOT depend on Infrastructure or Agent layers
- DTOs MUST use `record` types
- All async methods MUST accept `CancellationToken`
- Tests MUST use Shouldly for assertions
- Code MUST follow TDD (tests written before implementation)

### Selected Approach

**Approach**: In-place refactoring with strict validation
**Description**: Update all Service Bus components to require correlationId and agentId in the JSON body, validate agentId against configured agents, and rename all sourceId references to correlationId.
**Rationale**: This approach maintains backward compatibility with existing Redis persistence (just under a different key pattern) while enforcing stricter validation to prevent misconfigured messages from being processed.
**Trade-offs Accepted**: Existing conversations using the old Redis key pattern will not be found (start fresh). This is acceptable since Service Bus conversations are typically short-lived CI/CD interactions.

## Implementation Plan

### Domain/DTOs/ServiceBusPromptMessage.cs [edit]

**Purpose**: Define the incoming Service Bus message structure with all required fields.
**TOTAL CHANGES**: 1

**Changes**:
1. Add `correlationId` and `agentId` properties with JSON attributes (lines 7-14)

**Implementation Details**:
- Add `[JsonPropertyName("correlationId")]` property before `Prompt`
- Add `[JsonPropertyName("agentId")]` property before `Prompt`
- All properties use `required` modifier

**Reference Implementation**:
```csharp
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record ServiceBusPromptMessage
{
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [JsonPropertyName("sender")]
    public required string Sender { get; init; }
}
```

**Migration Pattern**:
```csharp
// BEFORE (lines 7-14):
public record ServiceBusPromptMessage
{
    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [JsonPropertyName("sender")]
    public required string Sender { get; init; }
}

// AFTER:
public record ServiceBusPromptMessage
{
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [JsonPropertyName("sender")]
    public required string Sender { get; init; }
}
```

**Test File**: `Tests/Unit/Infrastructure/Messaging/ServiceBusMessageParserTests.cs`
- Test: `Parse_ValidMessage_ReturnsParseSuccess` — Asserts: correlationId and agentId extracted from JSON body
- Test: `Parse_MissingCorrelationId_ReturnsParseFailure` — Asserts: failure with reason "MissingField"
- Test: `Parse_MissingAgentId_ReturnsParseFailure` — Asserts: failure with reason "MissingField"

**Success Criteria**:
- [ ] File compiles without errors
- [ ] Verification command: `dotnet build Domain/Domain.csproj`

**Dependencies**: None
**Provides**: `ServiceBusPromptMessage { CorrelationId, AgentId, Prompt, Sender }`

---

### Domain/DTOs/ParsedServiceBusMessage.cs [edit]

**Purpose**: Define the validated message record with renamed CorrelationId field.
**TOTAL CHANGES**: 1

**Changes**:
1. Rename `SourceId` parameter to `CorrelationId` (line 4)

**Implementation Details**:
- Simple rename in positional record

**Reference Implementation**:
```csharp
namespace Domain.DTOs;

public sealed record ParsedServiceBusMessage(
    string CorrelationId,
    string AgentId,
    string Prompt,
    string Sender);
```

**Migration Pattern**:
```csharp
// BEFORE (lines 3-7):
public sealed record ParsedServiceBusMessage(
    string Prompt,
    string Sender,
    string SourceId,
    string AgentId);

// AFTER:
public sealed record ParsedServiceBusMessage(
    string CorrelationId,
    string AgentId,
    string Prompt,
    string Sender);
```

**Test File**: `Tests/Unit/Infrastructure/Messaging/ServiceBusMessageParserTests.cs`
- Test: `Parse_ValidMessage_ReturnsParseSuccess` — Asserts: `success.Message.CorrelationId` equals expected value

**Success Criteria**:
- [ ] File compiles without errors
- [ ] Verification command: `dotnet build Domain/Domain.csproj`

**Dependencies**: None
**Provides**: `ParsedServiceBusMessage { CorrelationId, AgentId, Prompt, Sender }`

---

### Domain/DTOs/ServiceBusResponseMessage.cs [create]

**Purpose**: Define the public response message DTO with correlationId.
**TOTAL CHANGES**: 1

**Changes**:
1. Create new file with ServiceBusResponseMessage record

**Implementation Details**:
- Public record with JSON property name attributes
- Uses `correlationId` instead of `sourceId`
- Includes `agentId`, `response`, and `completedAt` fields

**Reference Implementation**:
```csharp
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record ServiceBusResponseMessage
{
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("response")]
    public required string Response { get; init; }

    [JsonPropertyName("completedAt")]
    public required DateTimeOffset CompletedAt { get; init; }
}
```

**Test File**: `Tests/Unit/Infrastructure/Messaging/ServiceBusResponseWriterTests.cs`
- Test: `WriteResponseAsync_SuccessfulSend_SerializesWithCorrelationId` — Asserts: JSON contains "correlationId" property

**Success Criteria**:
- [ ] File compiles without errors
- [ ] Verification command: `dotnet build Domain/Domain.csproj`

**Dependencies**: None
**Provides**: `ServiceBusResponseMessage { CorrelationId, AgentId, Response, CompletedAt }`

---

### Tests/Unit/Infrastructure/Messaging/ServiceBusMessageParserTests.cs [edit]

**Purpose**: Update tests to use new message format with correlationId and agentId in JSON body, add validation tests.
**TOTAL CHANGES**: 6

**Changes**:
1. Update `_parser` instantiation to accept `validAgentIds` list (line 10)
2. Update `CreateMessage` helper to put correlationId and agentId in JSON body instead of application properties (lines 119-136)
3. Update `Parse_ValidMessage_ReturnsParseSuccess` to verify CorrelationId (line 29)
4. Replace `Parse_MissingSourceId_GeneratesNewSourceId` with `Parse_MissingCorrelationId_ReturnsParseFailure` (lines 67-82)
5. Replace `Parse_MissingAgentId_UsesDefaultAgentId` with `Parse_MissingAgentId_ReturnsParseFailure` (lines 84-100)
6. Add `Parse_InvalidAgentId_ReturnsParseFailure` test

**Implementation Details**:
- Parser now takes `IReadOnlyList<string> validAgentIds` constructor argument
- Message body now includes `correlationId` and `agentId` fields
- No more application properties for sourceId/agentId

**Reference Implementation**:
```csharp
using Azure.Messaging.ServiceBus;
using Domain.DTOs;
using Infrastructure.Clients.Messaging.ServiceBus;
using Shouldly;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusMessageParserTests
{
    private readonly ServiceBusMessageParser _parser = new(["agent-456", "default-agent", "test-agent"]);

    [Fact]
    public void Parse_ValidMessage_ReturnsParseSuccess()
    {
        // Arrange
        var message = CreateMessage(
            correlationId: "correlation-123",
            agentId: "agent-456",
            prompt: "Hello",
            sender: "user1");

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseSuccess>();
        var success = (ParseSuccess)result;
        success.Message.Prompt.ShouldBe("Hello");
        success.Message.Sender.ShouldBe("user1");
        success.Message.CorrelationId.ShouldBe("correlation-123");
        success.Message.AgentId.ShouldBe("agent-456");
    }

    [Fact]
    public void Parse_MissingPrompt_ReturnsParseFailure()
    {
        // Arrange
        var message = CreateMessage(
            correlationId: "correlation-123",
            agentId: "agent-456",
            prompt: null,
            sender: "user1");

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("MissingField");
        failure.Details.ShouldContain("prompt");
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsParseFailure()
    {
        // Arrange
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("not json"),
            messageId: Guid.NewGuid().ToString());

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("DeserializationError");
    }

    [Fact]
    public void Parse_MissingCorrelationId_ReturnsParseFailure()
    {
        // Arrange
        var message = CreateMessage(
            correlationId: null,
            agentId: "agent-456",
            prompt: "Hello",
            sender: "user1");

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("MissingField");
        failure.Details.ShouldContain("correlationId");
    }

    [Fact]
    public void Parse_MissingAgentId_ReturnsParseFailure()
    {
        // Arrange
        var message = CreateMessage(
            correlationId: "correlation-123",
            agentId: null,
            prompt: "Hello",
            sender: "user1");

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("MissingField");
        failure.Details.ShouldContain("agentId");
    }

    [Fact]
    public void Parse_InvalidAgentId_ReturnsParseFailure()
    {
        // Arrange
        var message = CreateMessage(
            correlationId: "correlation-123",
            agentId: "unknown-agent",
            prompt: "Hello",
            sender: "user1");

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("InvalidAgentId");
        failure.Details.ShouldContain("unknown-agent");
    }

    [Fact]
    public void Parse_EmptyPrompt_ReturnsParseFailure()
    {
        // Arrange
        var message = CreateMessage(
            correlationId: "correlation-123",
            agentId: "agent-456",
            prompt: "",
            sender: "user1");

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("MissingField");
    }

    [Fact]
    public void Parse_EmptyCorrelationId_ReturnsParseFailure()
    {
        // Arrange
        var message = CreateMessage(
            correlationId: "",
            agentId: "agent-456",
            prompt: "Hello",
            sender: "user1");

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("MissingField");
        failure.Details.ShouldContain("correlationId");
    }

    [Fact]
    public void Parse_EmptyAgentId_ReturnsParseFailure()
    {
        // Arrange
        var message = CreateMessage(
            correlationId: "correlation-123",
            agentId: "",
            prompt: "Hello",
            sender: "user1");

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("MissingField");
        failure.Details.ShouldContain("agentId");
    }

    private static ServiceBusReceivedMessage CreateMessage(
        string? correlationId,
        string? agentId,
        string? prompt,
        string? sender)
    {
        var bodyObj = new Dictionary<string, object?>();
        if (correlationId is not null)
            bodyObj["correlationId"] = correlationId;
        if (agentId is not null)
            bodyObj["agentId"] = agentId;
        if (prompt is not null)
            bodyObj["prompt"] = prompt;
        if (sender is not null)
            bodyObj["sender"] = sender;

        var json = System.Text.Json.JsonSerializer.Serialize(bodyObj);

        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(json),
            messageId: Guid.NewGuid().ToString());
    }
}
```

**Success Criteria**:
- [ ] All tests compile
- [ ] Tests fail initially (RED phase) because parser not yet updated
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusMessageParserTests" --no-build` (should fail until parser is updated)

**Dependencies**: `Domain/DTOs/ServiceBusPromptMessage.cs`, `Domain/DTOs/ParsedServiceBusMessage.cs`
**Provides**: Test coverage for `ServiceBusMessageParser`

---

### Infrastructure/Clients/Messaging/ServiceBus/ServiceBusMessageParser.cs [edit]

**Purpose**: Update parser to read correlationId and agentId from JSON body, validate agentId against configured agents.
**TOTAL CHANGES**: 4

**Changes**:
1. Change constructor parameter from `string defaultAgentId` to `IReadOnlyList<string> validAgentIds` (line 7)
2. Remove application properties reading for sourceId/agentId (lines 43-49)
3. Extract correlationId and agentId from deserialized message body
4. Add validation for correlationId, agentId (existence and validity), prompt

**Implementation Details**:
- Constructor stores `validAgentIds` as `HashSet<string>` for O(1) lookup
- Validation order: deserialize -> check correlationId -> check agentId exists -> check agentId valid -> check prompt
- Return `ParseFailure("MissingField", ...)` for missing fields
- Return `ParseFailure("InvalidAgentId", ...)` for unknown agent

**Reference Implementation**:
```csharp
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Domain.DTOs;

namespace Infrastructure.Clients.Messaging.ServiceBus;

public sealed class ServiceBusMessageParser(IReadOnlyList<string> validAgentIds)
{
    private readonly HashSet<string> _validAgentIds = validAgentIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

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

        if (parsed is null)
        {
            return new ParseFailure("DeserializationError", "Message body deserialized to null");
        }

        if (string.IsNullOrEmpty(parsed.CorrelationId))
        {
            return new ParseFailure("MissingField", "Missing required 'correlationId' field");
        }

        if (string.IsNullOrEmpty(parsed.AgentId))
        {
            return new ParseFailure("MissingField", "Missing required 'agentId' field");
        }

        if (!_validAgentIds.Contains(parsed.AgentId))
        {
            return new ParseFailure("InvalidAgentId", $"Agent '{parsed.AgentId}' is not configured");
        }

        if (string.IsNullOrEmpty(parsed.Prompt))
        {
            return new ParseFailure("MissingField", "Missing required 'prompt' field");
        }

        return new ParseSuccess(new ParsedServiceBusMessage(
            parsed.CorrelationId,
            parsed.AgentId,
            parsed.Prompt,
            parsed.Sender));
    }
}
```

**Migration Pattern**:
```csharp
// BEFORE (line 7):
public sealed class ServiceBusMessageParser(string defaultAgentId)

// AFTER:
public sealed class ServiceBusMessageParser(IReadOnlyList<string> validAgentIds)
```

**Success Criteria**:
- [ ] File compiles without errors
- [ ] All `ServiceBusMessageParserTests` pass
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusMessageParserTests"`

**Dependencies**: `Tests/Unit/Infrastructure/Messaging/ServiceBusMessageParserTests.cs`, `Domain/DTOs/ServiceBusPromptMessage.cs`, `Domain/DTOs/ParsedServiceBusMessage.cs`
**Provides**: `ServiceBusMessageParser.Parse(ServiceBusReceivedMessage) -> ParseResult`

---

### Tests/Unit/Infrastructure/Messaging/ServiceBusSourceMapperTests.cs [edit]

**Purpose**: Rename all sourceId references to correlationId, update Redis key pattern.
**TOTAL CHANGES**: 5

**Changes**:
1. Rename test method `GetOrCreateMappingAsync_NewSourceId_CreatesMappingAndTopic` to `GetOrCreateMappingAsync_NewCorrelationId_CreatesMappingAndTopic` (line 33)
2. Rename test method `GetOrCreateMappingAsync_ExistingSourceId_ReturnsCachedMapping` to `GetOrCreateMappingAsync_ExistingCorrelationId_ReturnsCachedMapping` (line 81)
3. Update Redis key pattern from `sb-source:{agentId}:{sourceId}` to `sb-correlation:{agentId}:{correlationId}` (lines 39, 91)
4. Rename `TryGetSourceId` tests to `TryGetCorrelationId` (lines 158, 189)
5. Update variable names from `sourceId` to `correlationId` throughout

**Implementation Details**:
- All `sourceId` variable names become `correlationId`
- Redis key pattern changes
- Method name changes for `TryGetCorrelationId`

**Reference Implementation**:
```csharp
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusConversationMapperTests
{
    private readonly Mock<IDatabase> _dbMock;
    private readonly Mock<IThreadStateStore> _threadStateStoreMock;
    private readonly ServiceBusConversationMapper _mapper;

    public ServiceBusConversationMapperTests()
    {
        var redisMock = new Mock<IConnectionMultiplexer>();
        _dbMock = new Mock<IDatabase>();
        _threadStateStoreMock = new Mock<IThreadStateStore>();
        var loggerMock = new Mock<ILogger<ServiceBusConversationMapper>>();

        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_dbMock.Object);

        _mapper = new ServiceBusConversationMapper(
            redisMock.Object,
            _threadStateStoreMock.Object,
            loggerMock.Object);
    }

    [Fact]
    public async Task GetOrCreateMappingAsync_NewCorrelationId_CreatesMappingAndTopic()
    {
        // Arrange
        const string correlationId = "cicd-pipeline-1";
        const string agentId = "default";
        var redisKey = $"sb-correlation:{agentId}:{correlationId}";

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
        var (chatId, threadId, topicId, isNew) = await _mapper.GetOrCreateMappingAsync(correlationId, agentId);

        // Assert
        isNew.ShouldBeTrue();
        chatId.ShouldBeGreaterThan(0);
        threadId.ShouldBeGreaterThan(0);
        topicId.ShouldNotBeNullOrEmpty();

        _threadStateStoreMock.Verify(s => s.SaveTopicAsync(
            It.Is<TopicMetadata>(t =>
                t.Name == $"[SB] {correlationId}" &&
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
    public async Task GetOrCreateMappingAsync_ExistingCorrelationId_ReturnsCachedMapping()
    {
        // Arrange
        const string correlationId = "cicd-pipeline-1";
        const string agentId = "default";
        const long expectedChatId = 12345;
        const long expectedThreadId = 67890;
        const string expectedTopicId = "abc123";
        var redisKey = $"sb-correlation:{agentId}:{correlationId}";
        var cachedJson =
            $"{{\"ChatId\":{expectedChatId},\"ThreadId\":{expectedThreadId},\"TopicId\":\"{expectedTopicId}\"}}";

        _dbMock.Setup(db => db.StringGetAsync(redisKey, It.IsAny<CommandFlags>()))
            .ReturnsAsync(cachedJson);

        // Act
        var (chatId, threadId, topicId, isNew) = await _mapper.GetOrCreateMappingAsync(correlationId, agentId);

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
        const string correlationId = "shared-correlation";
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
        var (chatId1, _, _, isNew1) = await _mapper.GetOrCreateMappingAsync(correlationId, agentId1);
        var (chatId2, _, _, isNew2) = await _mapper.GetOrCreateMappingAsync(correlationId, agentId2);

        // Assert
        isNew1.ShouldBeTrue();
        isNew2.ShouldBeTrue();
        chatId1.ShouldNotBe(chatId2);

        _dbMock.Verify(db => db.StringSetAsync(
            $"sb-correlation:{agentId1}:{correlationId}",
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);

        _dbMock.Verify(db => db.StringSetAsync(
            $"sb-correlation:{agentId2}:{correlationId}",
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task TryGetCorrelationId_AfterMapping_ReturnsCorrelationId()
    {
        // Arrange
        const string correlationId = "test-correlation";
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
        var (chatId, _, _, _) = await _mapper.GetOrCreateMappingAsync(correlationId, agentId);
        var found = _mapper.TryGetCorrelationId(chatId, out var retrievedCorrelationId);

        // Assert
        found.ShouldBeTrue();
        retrievedCorrelationId.ShouldBe(correlationId);
    }

    [Fact]
    public void TryGetCorrelationId_UnknownChatId_ReturnsFalse()
    {
        // Act
        var found = _mapper.TryGetCorrelationId(999999, out _);

        // Assert
        found.ShouldBeFalse();
    }
}
```

**Success Criteria**:
- [ ] All tests compile
- [ ] Tests fail initially (RED phase) because mapper not yet updated
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusConversationMapperTests" --no-build` (should fail until mapper is updated)

**Dependencies**: `Domain/DTOs/ParsedServiceBusMessage.cs`
**Provides**: Test coverage for `ServiceBusConversationMapper`

---

### Infrastructure/Clients/Messaging/ServiceBus/ServiceBusSourceMapper.cs [edit]

**Purpose**: Rename sourceId to correlationId throughout, update Redis key pattern.
**TOTAL CHANGES**: 4

**Changes**:
1. Rename `_chatIdToSourceId` dictionary to `_chatIdToCorrelationId` (line 17)
2. Rename method parameter `sourceId` to `correlationId` in `GetOrCreateMappingAsync` (line 19)
3. Update Redis key pattern from `sb-source:{agentId}:{sourceId}` to `sb-correlation:{agentId}:{correlationId}` (line 24)
4. Rename `TryGetSourceId` to `TryGetCorrelationId` (line 69)

**Implementation Details**:
- All `sourceId` references become `correlationId`
- Topic name pattern changes from `[SB] {sourceId}` to `[SB] {correlationId}` (line 43)

**Reference Implementation**:
```csharp
using System.Collections.Concurrent;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure.Clients.Messaging.ServiceBus;

public sealed class ServiceBusConversationMapper(
    IConnectionMultiplexer redis,
    IThreadStateStore threadStateStore,
    ILogger<ServiceBusConversationMapper> logger)
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly ConcurrentDictionary<long, string> _chatIdToCorrelationId = new();

    public async Task<(long ChatId, long ThreadId, string TopicId, bool IsNew)> GetOrCreateMappingAsync(
        string correlationId,
        string agentId,
        CancellationToken ct = default)
    {
        var redisKey = $"sb-correlation:{agentId}:{correlationId}";
        var existingJson = await _db.StringGetAsync(redisKey);

        if (existingJson.HasValue)
        {
            var existing = JsonSerializer.Deserialize<CorrelationMapping>(existingJson.ToString());
            if (existing is not null)
            {
                logger.LogDebug(
                    "Found existing mapping for correlationId={CorrelationId}: chatId={ChatId}, threadId={ThreadId}",
                    correlationId, existing.ChatId, existing.ThreadId);
                _chatIdToCorrelationId[existing.ChatId] = correlationId;
                return (existing.ChatId, existing.ThreadId, existing.TopicId, false);
            }
        }

        var topicId = TopicIdHasher.GenerateTopicId();
        var chatId = TopicIdHasher.GetChatIdForTopic(topicId);
        var threadId = TopicIdHasher.GetThreadIdForTopic(topicId);
        var topicName = $"[SB] {correlationId}";

        var topic = new TopicMetadata(
            TopicId: topicId,
            ChatId: chatId,
            ThreadId: threadId,
            AgentId: agentId,
            Name: topicName,
            CreatedAt: DateTimeOffset.UtcNow,
            LastMessageAt: null);

        await threadStateStore.SaveTopicAsync(topic);

        var mapping = new CorrelationMapping(chatId, threadId, topicId);
        var mappingJson = JsonSerializer.Serialize(mapping);
        await _db.StringSetAsync(redisKey, mappingJson, TimeSpan.FromDays(30), false);

        _chatIdToCorrelationId[chatId] = correlationId;

        logger.LogInformation(
            "Created new mapping for correlationId={CorrelationId}: chatId={ChatId}, threadId={ThreadId}, topicId={TopicId}",
            correlationId, chatId, threadId, topicId);

        return (chatId, threadId, topicId, true);
    }

    public bool TryGetCorrelationId(long chatId, out string correlationId)
    {
        return _chatIdToCorrelationId.TryGetValue(chatId, out correlationId!);
    }

    private sealed record CorrelationMapping(long ChatId, long ThreadId, string TopicId);
}
```

**Migration Pattern**:
```csharp
// BEFORE (line 17):
private readonly ConcurrentDictionary<long, string> _chatIdToSourceId = new();

// AFTER:
private readonly ConcurrentDictionary<long, string> _chatIdToCorrelationId = new();

// BEFORE (line 24):
var redisKey = $"sb-source:{agentId}:{sourceId}";

// AFTER:
var redisKey = $"sb-correlation:{agentId}:{correlationId}";

// BEFORE (line 69-72):
public bool TryGetSourceId(long chatId, out string sourceId)
{
    return _chatIdToSourceId.TryGetValue(chatId, out sourceId!);
}

// AFTER:
public bool TryGetCorrelationId(long chatId, out string correlationId)
{
    return _chatIdToCorrelationId.TryGetValue(chatId, out correlationId!);
}
```

**Success Criteria**:
- [ ] File compiles without errors
- [ ] All `ServiceBusConversationMapperTests` pass
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusConversationMapperTests"`

**Dependencies**: `Tests/Unit/Infrastructure/Messaging/ServiceBusSourceMapperTests.cs`, `Domain/DTOs/ParsedServiceBusMessage.cs`
**Provides**: `ServiceBusConversationMapper.GetOrCreateMappingAsync(correlationId, agentId)`, `ServiceBusConversationMapper.TryGetCorrelationId(chatId)`

---

### Tests/Unit/Infrastructure/Messaging/ServiceBusPromptReceiverTests.cs [edit]

**Purpose**: Update tests to use CorrelationId instead of SourceId.
**TOTAL CHANGES**: 3

**Changes**:
1. Update `ParsedServiceBusMessage` constructor call to use `CorrelationId` parameter name (lines 54, 79, 104-105)
2. Rename `TryGetSourceId` test to `TryGetCorrelationId` (line 76)
3. Update assertion to check `correlationId` instead of `sourceId` (line 96-97)

**Implementation Details**:
- `ParsedServiceBusMessage` constructor parameter order is now: `CorrelationId, AgentId, Prompt, Sender`
- Method name changes from `TryGetSourceId` to `TryGetCorrelationId`

**Reference Implementation**:
```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusPromptReceiverTests
{
    private readonly ServiceBusPromptReceiver _receiver;

    public ServiceBusPromptReceiverTests()
    {
        var redisMock = new Mock<IConnectionMultiplexer>();
        var dbMock = new Mock<IDatabase>();
        var threadStateStoreMock = new Mock<IThreadStateStore>();
        var mapperLoggerMock = new Mock<ILogger<ServiceBusConversationMapper>>();
        var receiverLoggerMock = new Mock<ILogger<ServiceBusPromptReceiver>>();

        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(dbMock.Object);

        dbMock.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        dbMock.Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        threadStateStoreMock.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Returns(Task.CompletedTask);

        var mapper = new ServiceBusConversationMapper(
            redisMock.Object,
            threadStateStoreMock.Object,
            mapperLoggerMock.Object);

        _receiver = new ServiceBusPromptReceiver(mapper, receiverLoggerMock.Object);
    }

    [Fact]
    public async Task EnqueueAsync_ValidMessage_WritesToChannel()
    {
        // Arrange
        var message = new ParsedServiceBusMessage("correlation-123", "agent-1", "Hello", "user1");
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
    public async Task TryGetCorrelationId_AfterEnqueue_ReturnsCorrelationId()
    {
        // Arrange
        var message = new ParsedServiceBusMessage("correlation-123", "agent-1", "Hello", "user1");
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
        var found = _receiver.TryGetCorrelationId(prompt.ChatId, out var correlationId);
        found.ShouldBeTrue();
        correlationId.ShouldBe("correlation-123");
    }

    [Fact]
    public async Task EnqueueAsync_MultipleMessages_IncrementsMessageId()
    {
        // Arrange
        var message1 = new ParsedServiceBusMessage("correlation-1", "agent-1", "First", "user1");
        var message2 = new ParsedServiceBusMessage("correlation-2", "agent-1", "Second", "user1");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act
        await _receiver.EnqueueAsync(message1, cts.Token);
        await _receiver.EnqueueAsync(message2, cts.Token);

        // Assert
        var prompts = new List<ChatPrompt>();
        await foreach (var p in _receiver.ReadPromptsAsync(cts.Token))
        {
            prompts.Add(p);
            if (prompts.Count >= 2)
            {
                break;
            }
        }

        prompts[0].MessageId.ShouldBeLessThan(prompts[1].MessageId);
    }
}
```

**Success Criteria**:
- [ ] All tests compile
- [ ] Tests fail initially (RED phase) because receiver not yet updated
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusPromptReceiverTests" --no-build` (should fail until receiver is updated)

**Dependencies**: `Domain/DTOs/ParsedServiceBusMessage.cs`, `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusSourceMapper.cs`
**Provides**: Test coverage for `ServiceBusPromptReceiver`

---

### Infrastructure/Clients/Messaging/ServiceBus/ServiceBusPromptReceiver.cs [edit]

**Purpose**: Update to use CorrelationId instead of SourceId.
**TOTAL CHANGES**: 2

**Changes**:
1. Update `EnqueueAsync` to use `message.CorrelationId` (line 22)
2. Rename `TryGetSourceId` to `TryGetCorrelationId` (lines 42-45)

**Implementation Details**:
- Uses `ParsedServiceBusMessage.CorrelationId` instead of `SourceId`
- Method delegates to mapper's `TryGetCorrelationId`

**Reference Implementation**:
```csharp
using System.Threading.Channels;
using Domain.DTOs;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging.ServiceBus;

public class ServiceBusPromptReceiver(
    ServiceBusConversationMapper conversationMapper,
    ILogger<ServiceBusPromptReceiver> logger)
{
    private readonly Channel<ChatPrompt> _channel = Channel.CreateUnbounded<ChatPrompt>();
    private int _messageIdCounter;

    public IAsyncEnumerable<ChatPrompt> ReadPromptsAsync(CancellationToken ct)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }

    public async Task EnqueueAsync(ParsedServiceBusMessage message, CancellationToken ct)
    {
        var (chatId, threadId, _, _) = await conversationMapper.GetOrCreateMappingAsync(
            message.CorrelationId, message.AgentId, ct);

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
            "Enqueued prompt from Service Bus: correlationId={CorrelationId}, chatId={ChatId}",
            message.CorrelationId, chatId);

        await _channel.Writer.WriteAsync(prompt, ct);
    }

    public virtual bool TryGetCorrelationId(long chatId, out string correlationId)
    {
        return conversationMapper.TryGetCorrelationId(chatId, out correlationId);
    }
}
```

**Migration Pattern**:
```csharp
// BEFORE (line 22):
message.SourceId, message.AgentId, ct);

// AFTER:
message.CorrelationId, message.AgentId, ct);

// BEFORE (lines 42-45):
public virtual bool TryGetSourceId(long chatId, out string sourceId)
{
    return conversationMapper.TryGetSourceId(chatId, out sourceId);
}

// AFTER:
public virtual bool TryGetCorrelationId(long chatId, out string correlationId)
{
    return conversationMapper.TryGetCorrelationId(chatId, out correlationId);
}
```

**Success Criteria**:
- [ ] File compiles without errors
- [ ] All `ServiceBusPromptReceiverTests` pass
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusPromptReceiverTests"`

**Dependencies**: `Tests/Unit/Infrastructure/Messaging/ServiceBusPromptReceiverTests.cs`, `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusSourceMapper.cs`, `Domain/DTOs/ParsedServiceBusMessage.cs`
**Provides**: `ServiceBusPromptReceiver.TryGetCorrelationId(chatId)`

---

### Tests/Unit/Infrastructure/Messaging/ServiceBusResponseWriterTests.cs [edit]

**Purpose**: Update tests to use correlationId parameter instead of sourceId.
**TOTAL CHANGES**: 2

**Changes**:
1. Update `WriteResponseAsync` calls to use `correlationId` parameter name (lines 24, 55, 75, 97)
2. Add test to verify JSON output contains `correlationId` field

**Implementation Details**:
- Parameter name changes from `sourceId` to `correlationId`
- Verify serialized JSON contains "correlationId" property

**Reference Implementation**:
```csharp
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Infrastructure.Clients.Messaging.ServiceBus;
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
        ServiceBusMessage? capturedMessage = null;

        senderMock.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((m, _) => capturedMessage = m)
            .Returns(Task.CompletedTask);

        var writer = new ServiceBusResponseWriter(senderMock.Object, loggerMock.Object);

        // Act
        await writer.WriteResponseAsync("correlation-123", "agent-1", "Hello response");

        // Assert
        senderMock.Verify(s => s.SendMessageAsync(
            It.Is<ServiceBusMessage>(m => m.ContentType == "application/json"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify JSON contains correlationId
        capturedMessage.ShouldNotBeNull();
        var json = capturedMessage.Body.ToString();
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("correlationId").GetString().ShouldBe("correlation-123");
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
        await writer.WriteResponseAsync("correlation-123", "agent-1", "Hello response");

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
            await writer.WriteResponseAsync("correlation-123", "agent-1", "Hello response"));

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
            await writer.WriteResponseAsync("correlation-123", "agent-1", "Hello response"));

        // Assert - called only once (no retry for non-transient)
        senderMock.Verify(s => s.SendMessageAsync(
            It.IsAny<ServiceBusMessage>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

**Success Criteria**:
- [ ] All tests compile
- [ ] Tests fail initially (RED phase) because writer not yet updated
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusResponseWriterTests" --no-build` (should fail until writer is updated)

**Dependencies**: `Domain/DTOs/ServiceBusResponseMessage.cs`
**Provides**: Test coverage for `ServiceBusResponseWriter`

---

### Infrastructure/Clients/Messaging/ServiceBus/ServiceBusResponseWriter.cs [edit]

**Purpose**: Update to use correlationId parameter and public ServiceBusResponseMessage DTO.
**TOTAL CHANGES**: 3

**Changes**:
1. Rename `sourceId` parameter to `correlationId` in `WriteResponseAsync` (line 42)
2. Use public `Domain.DTOs.ServiceBusResponseMessage` instead of private record (lines 49-55)
3. Remove private `ServiceBusResponseMessage` record (lines 75-81)
4. Update log message to use `correlationId` (lines 67, 71)

**Implementation Details**:
- Import `Domain.DTOs` for `ServiceBusResponseMessage`
- Use `CorrelationId` property instead of `SourceId`

**Reference Implementation**:
```csharp
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Domain.DTOs;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Infrastructure.Clients.Messaging.ServiceBus;

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

    public virtual async Task WriteResponseAsync(
        string correlationId,
        string agentId,
        string response,
        CancellationToken ct = default)
    {
        try
        {
            var responseMessage = new ServiceBusResponseMessage
            {
                CorrelationId = correlationId,
                AgentId = agentId,
                Response = response,
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

            _logger.LogDebug("Sent response to queue for correlationId={CorrelationId}", correlationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send response to queue after retries for correlationId={CorrelationId}", correlationId);
        }
    }
}
```

**Migration Pattern**:
```csharp
// BEFORE (line 42):
public virtual async Task WriteResponseAsync(
    string sourceId,

// AFTER:
public virtual async Task WriteResponseAsync(
    string correlationId,

// BEFORE (lines 49-55):
var responseMessage = new ServiceBusResponseMessage
{
    SourceId = sourceId,
    Response = response,
    AgentId = agentId,
    CompletedAt = DateTimeOffset.UtcNow
};

// AFTER:
var responseMessage = new ServiceBusResponseMessage
{
    CorrelationId = correlationId,
    AgentId = agentId,
    Response = response,
    CompletedAt = DateTimeOffset.UtcNow
};
```

**Success Criteria**:
- [ ] File compiles without errors
- [ ] All `ServiceBusResponseWriterTests` pass
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusResponseWriterTests"`

**Dependencies**: `Tests/Unit/Infrastructure/Messaging/ServiceBusResponseWriterTests.cs`, `Domain/DTOs/ServiceBusResponseMessage.cs`
**Provides**: `ServiceBusResponseWriter.WriteResponseAsync(correlationId, agentId, response, ct)`

---

### Tests/Unit/Infrastructure/Messaging/ServiceBusResponseHandlerTests.cs [edit]

**Purpose**: Remove defaultAgentId from constructor, rename TryGetSourceId to TryGetCorrelationId.
**TOTAL CHANGES**: 3

**Changes**:
1. Update `CreateHandler` to not pass `defaultAgentId` (lines 147-152)
2. Rename `TryGetSourceId` mock setup to `TryGetCorrelationId` (lines 18-23, 44, 67, 94, 121)
3. Remove test `ProcessAsync_NullAgentId_UsesDefaultAgentId` since defaultAgentId is removed (lines 114-139)
4. Update `GetSourceId` references to `GetCorrelationId` (line 18)

**Implementation Details**:
- Handler no longer takes `defaultAgentId` in constructor
- Mock setup uses `TryGetCorrelationId` method name
- Test for null agentId fallback is removed (agentId is now required in parser)

**Reference Implementation**:
```csharp
using Domain.Agents;
using Domain.DTOs;
using Infrastructure.Clients.Messaging.ServiceBus;
using Microsoft.Agents.AI;
using Moq;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusResponseHandlerTests
{
    [Fact]
    public async Task ProcessAsync_CompletedResponse_WritesToServiceBus()
    {
        // Arrange
        var (handler, receiverMock, writerMock) = CreateHandler();
        var chatId = 123L;

        receiverMock.Setup(r => r.TryGetCorrelationId(chatId, out It.Ref<string>.IsAny))
            .Returns((long _, out string s) =>
            {
                s = "correlation-123";
                return true;
            });

        var updates = CreateUpdates(chatId, "agent-1", new AiResponse { Content = "Hello World" });

        // Act
        await handler.ProcessAsync(updates, CancellationToken.None);

        // Assert
        writerMock.Verify(w => w.WriteResponseAsync(
            "correlation-123",
            "agent-1",
            "Hello World",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_UnknownChatId_SkipsUpdate()
    {
        // Arrange
        var (handler, receiverMock, writerMock) = CreateHandler();

        receiverMock.Setup(r => r.TryGetCorrelationId(It.IsAny<long>(), out It.Ref<string>.IsAny))
            .Returns(false);

        var updates = CreateUpdates(999, "agent-1", new AiResponse { Content = "Hello" });

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
    public async Task ProcessAsync_NullAiResponse_DoesNotWrite()
    {
        // Arrange
        var (handler, receiverMock, writerMock) = CreateHandler();
        var chatId = 123L;

        receiverMock.Setup(r => r.TryGetCorrelationId(chatId, out It.Ref<string>.IsAny))
            .Returns((long _, out string s) =>
            {
                s = "correlation-123";
                return true;
            });

        var updates = CreateUpdates(chatId, "agent-1", null);

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
    public async Task ProcessAsync_EmptyContent_DoesNotWrite()
    {
        // Arrange
        var (handler, receiverMock, writerMock) = CreateHandler();
        var chatId = 123L;

        receiverMock.Setup(r => r.TryGetCorrelationId(chatId, out It.Ref<string>.IsAny))
            .Returns((long _, out string s) =>
            {
                s = "correlation-123";
                return true;
            });

        var updates = CreateUpdates(chatId, "agent-1", new AiResponse { Content = "" });

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
            writerMock.Object);

        return (handler, receiverMock, writerMock);
    }

    private static async IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> CreateUpdates(
        long chatId,
        string? agentId,
        AiResponse? response)
    {
        await Task.CompletedTask;
        yield return (new AgentKey(chatId, 1, agentId), new AgentResponseUpdate(), response, MessageSource.ServiceBus);
    }
}
```

**Success Criteria**:
- [ ] All tests compile
- [ ] Tests fail initially (RED phase) because handler not yet updated
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusResponseHandlerTests" --no-build` (should fail until handler is updated)

**Dependencies**: `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusPromptReceiver.cs`, `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusResponseWriter.cs`
**Provides**: Test coverage for `ServiceBusResponseHandler`

---

### Infrastructure/Clients/Messaging/ServiceBus/ServiceBusResponseHandler.cs [edit]

**Purpose**: Remove defaultAgentId parameter, rename TryGetSourceId to TryGetCorrelationId.
**TOTAL CHANGES**: 3

**Changes**:
1. Remove `string defaultAgentId` from constructor (line 10)
2. Rename `GetSourceId` to `GetCorrelationId` (lines 18, 29)
3. Update `TryGetSourceId` call to `TryGetCorrelationId` (line 31)
4. Use `key.AgentId!` directly since agentId is now required (line 25)

**Implementation Details**:
- No fallback to defaultAgentId since agentId is validated by parser
- AgentId is guaranteed to be non-null by parser validation

**Reference Implementation**:
```csharp
using Domain.Agents;
using Domain.DTOs;
using Microsoft.Agents.AI;

namespace Infrastructure.Clients.Messaging.ServiceBus;

public class ServiceBusResponseHandler(
    ServiceBusPromptReceiver promptReceiver,
    ServiceBusResponseWriter responseWriter)
{
    public async Task ProcessAsync(
        IAsyncEnumerable<(AgentKey Key, AgentResponseUpdate Update, AiResponse? Response, MessageSource Source)>
            updates,
        CancellationToken ct)
    {
        var completedResponses = updates
            .Select(x => (x.Key, x.Response?.Content, CorrelationId: GetCorrelationId(x.Key.ChatId)))
            .Where(x => !string.IsNullOrEmpty(x.Content) && x.CorrelationId is not null)
            .Select(x => (x.Key, Content: x.Content!, CorrelationId: x.CorrelationId!))
            .WithCancellation(ct);

        await foreach (var (key, content, correlationId) in completedResponses)
        {
            await responseWriter.WriteResponseAsync(correlationId, key.AgentId!, content, ct);
        }
    }

    private string? GetCorrelationId(long chatId)
    {
        return promptReceiver.TryGetCorrelationId(chatId, out var correlationId)
            ? correlationId
            : null;
    }
}
```

**Migration Pattern**:
```csharp
// BEFORE (lines 7-10):
public class ServiceBusResponseHandler(
    ServiceBusPromptReceiver promptReceiver,
    ServiceBusResponseWriter responseWriter,
    string defaultAgentId)

// AFTER:
public class ServiceBusResponseHandler(
    ServiceBusPromptReceiver promptReceiver,
    ServiceBusResponseWriter responseWriter)

// BEFORE (line 25):
await responseWriter.WriteResponseAsync(sourceId, key.AgentId ?? defaultAgentId, content, ct);

// AFTER:
await responseWriter.WriteResponseAsync(correlationId, key.AgentId!, content, ct);
```

**Success Criteria**:
- [ ] File compiles without errors
- [ ] All `ServiceBusResponseHandlerTests` pass
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusResponseHandlerTests"`

**Dependencies**: `Tests/Unit/Infrastructure/Messaging/ServiceBusResponseHandlerTests.cs`, `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusPromptReceiver.cs`, `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusResponseWriter.cs`
**Provides**: `ServiceBusResponseHandler.ProcessAsync(updates, ct)`

---

### Infrastructure/Clients/Messaging/ServiceBus/ServiceBusChatMessengerClient.cs [edit]

**Purpose**: Remove defaultAgentId parameter from constructor.
**TOTAL CHANGES**: 2

**Changes**:
1. Remove `string defaultAgentId` from constructor (line 11)
2. Update `CreateTopicIfNeededAsync` to use `agentId` parameter directly without fallback (line 41)

**Implementation Details**:
- No fallback to defaultAgentId since agentId is validated by parser
- `CreateTopicIfNeededAsync` returns agentId directly (or throws if null, which shouldn't happen for ServiceBus source)

**Reference Implementation**:
```csharp
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Agents.AI;

namespace Infrastructure.Clients.Messaging.ServiceBus;

public sealed class ServiceBusChatMessengerClient(
    ServiceBusPromptReceiver promptReceiver,
    ServiceBusResponseHandler responseHandler) : IChatMessengerClient
{
    public bool SupportsScheduledNotifications => false;
    public MessageSource Source => MessageSource.ServiceBus;

    public IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken)
    {
        return promptReceiver.ReadPromptsAsync(cancellationToken);
    }

    public Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
        CancellationToken cancellationToken)
    {
        return responseHandler.ProcessAsync(updates, cancellationToken);
    }

    public Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId, CancellationToken cancellationToken)
    {
        return Task.FromResult(false);
    }

    public Task<AgentKey> CreateTopicIfNeededAsync(
        MessageSource source,
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        return Task.FromResult(new AgentKey(chatId ?? 0, threadId ?? 0, agentId));
    }

    public Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
```

**Migration Pattern**:
```csharp
// BEFORE (lines 8-11):
public sealed class ServiceBusChatMessengerClient(
    ServiceBusPromptReceiver promptReceiver,
    ServiceBusResponseHandler responseHandler,
    string defaultAgentId) : IChatMessengerClient

// AFTER:
public sealed class ServiceBusChatMessengerClient(
    ServiceBusPromptReceiver promptReceiver,
    ServiceBusResponseHandler responseHandler) : IChatMessengerClient

// BEFORE (line 41):
return Task.FromResult(new AgentKey(chatId ?? 0, threadId ?? 0, agentId ?? defaultAgentId));

// AFTER:
ArgumentNullException.ThrowIfNull(agentId);
return Task.FromResult(new AgentKey(chatId ?? 0, threadId ?? 0, agentId));
```

**Success Criteria**:
- [ ] File compiles without errors
- [ ] Verification command: `dotnet build Infrastructure/Infrastructure.csproj`

**Dependencies**: `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusResponseHandler.cs`, `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusPromptReceiver.cs`
**Provides**: `ServiceBusChatMessengerClient` without `defaultAgentId`

---

### Agent/Modules/InjectorModule.cs [edit]

**Purpose**: Update AddServiceBusClient to accept agent definitions and extract valid agent IDs for parser.
**TOTAL CHANGES**: 4

**Changes**:
1. Change `AddServiceBusClient` signature from `string defaultAgentId` to `IReadOnlyList<AgentDefinition> agents` (line 177)
2. Extract valid agent IDs: `var validAgentIds = agents.Select(a => a.Id).ToList()` (line 179)
3. Pass `validAgentIds` to `ServiceBusMessageParser` constructor (line 196)
4. Remove `defaultAgentId` from `ServiceBusResponseHandler` and `ServiceBusChatMessengerClient` constructors (lines 203-210)
5. Update call site to pass `settings.Agents` instead of `settings.Agents[0].Id` (line 170)

**Implementation Details**:
- `AddServiceBusClient` receives full agent list to extract IDs
- Parser validates against all configured agent IDs
- Handler and client no longer need defaultAgentId

**Reference Implementation**:
```csharp
// ... (unchanged imports and class declaration)

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
                return services.AddServiceBusClient(settings.ServiceBus, settings.Agents);
            }

            return services
                .AddSingleton<IChatMessengerClient>(sp => sp.GetRequiredService<WebChatMessengerClient>());
        }

        private IServiceCollection AddServiceBusClient(ServiceBusSettings sbSettings, IReadOnlyList<AgentDefinition> agents)
        {
            var validAgentIds = agents.Select(a => a.Id).ToList();

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
                .AddSingleton(_ => new ServiceBusMessageParser(validAgentIds))
                .AddSingleton(sp => new ServiceBusPromptReceiver(
                    sp.GetRequiredService<ServiceBusConversationMapper>(),
                    sp.GetRequiredService<ILogger<ServiceBusPromptReceiver>>()))
                .AddSingleton(sp => new ServiceBusResponseWriter(
                    sp.GetRequiredService<ServiceBusSender>(),
                    sp.GetRequiredService<ILogger<ServiceBusResponseWriter>>()))
                .AddSingleton(sp => new ServiceBusResponseHandler(
                    sp.GetRequiredService<ServiceBusPromptReceiver>(),
                    sp.GetRequiredService<ServiceBusResponseWriter>()))
                .AddSingleton(sp => new ServiceBusChatMessengerClient(
                    sp.GetRequiredService<ServiceBusPromptReceiver>(),
                    sp.GetRequiredService<ServiceBusResponseHandler>()))
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

**Migration Pattern**:
```csharp
// BEFORE (line 170):
return services.AddServiceBusClient(settings.ServiceBus, settings.Agents[0].Id);

// AFTER:
return services.AddServiceBusClient(settings.ServiceBus, settings.Agents);

// BEFORE (line 177):
private IServiceCollection AddServiceBusClient(ServiceBusSettings sbSettings, string defaultAgentId)

// AFTER:
private IServiceCollection AddServiceBusClient(ServiceBusSettings sbSettings, IReadOnlyList<AgentDefinition> agents)

// BEFORE (line 196):
.AddSingleton(_ => new ServiceBusMessageParser(defaultAgentId))

// AFTER:
var validAgentIds = agents.Select(a => a.Id).ToList();
// ...
.AddSingleton(_ => new ServiceBusMessageParser(validAgentIds))

// BEFORE (lines 203-210):
.AddSingleton(sp => new ServiceBusResponseHandler(
    sp.GetRequiredService<ServiceBusPromptReceiver>(),
    sp.GetRequiredService<ServiceBusResponseWriter>(),
    defaultAgentId))
.AddSingleton(sp => new ServiceBusChatMessengerClient(
    sp.GetRequiredService<ServiceBusPromptReceiver>(),
    sp.GetRequiredService<ServiceBusResponseHandler>(),
    defaultAgentId))

// AFTER:
.AddSingleton(sp => new ServiceBusResponseHandler(
    sp.GetRequiredService<ServiceBusPromptReceiver>(),
    sp.GetRequiredService<ServiceBusResponseWriter>()))
.AddSingleton(sp => new ServiceBusChatMessengerClient(
    sp.GetRequiredService<ServiceBusPromptReceiver>(),
    sp.GetRequiredService<ServiceBusResponseHandler>()))
```

**Success Criteria**:
- [ ] File compiles without errors
- [ ] Application starts successfully with Service Bus configured
- [ ] Verification command: `dotnet build Agent/Agent.csproj`

**Dependencies**: `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusMessageParser.cs`, `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusResponseHandler.cs`, `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusChatMessengerClient.cs`
**Provides**: DI registration for Service Bus components with agent validation

---

### Tests/Integration/Fixtures/ServiceBusFixture.cs [edit]

**Purpose**: Update fixture to use new message format and constructor signatures.
**TOTAL CHANGES**: 4

**Changes**:
1. Update `SendPromptAsync` to put correlationId and agentId in JSON body (lines 67-91)
2. Update `CreateClientAndHost` to pass `validAgentIds` list to `ServiceBusMessageParser` (line 158)
3. Remove `defaultAgentId` from `ServiceBusResponseHandler` constructor (lines 142-145)
4. Remove `defaultAgentId` from `ServiceBusChatMessengerClient` constructor (lines 147-150)

**Implementation Details**:
- Message body now includes all required fields
- Parser receives list of valid agent IDs
- Handler and client no longer need defaultAgentId

**Reference Implementation**:
```csharp
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
        string? correlationId = null,
        string? agentId = null)
    {
        var messageBody = new
        {
            correlationId = correlationId ?? Guid.NewGuid().ToString("N"),
            agentId = agentId ?? DefaultAgentId,
            prompt,
            sender
        };
        var json = JsonSerializer.Serialize(messageBody);
        var message = new ServiceBusMessage(BinaryData.FromString(json))
        {
            ContentType = "application/json"
        };

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
            responseWriter);

        var client = new ServiceBusChatMessengerClient(
            promptReceiver,
            responseHandler);

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
```

**Migration Pattern**:
```csharp
// BEFORE (lines 72-78):
var messageBody = new { prompt, sender };
// ...
if (sourceId is not null)
{
    message.ApplicationProperties["sourceId"] = sourceId;
}

// AFTER:
var messageBody = new
{
    correlationId = correlationId ?? Guid.NewGuid().ToString("N"),
    agentId = agentId ?? DefaultAgentId,
    prompt,
    sender
};

// BEFORE (line 158):
var messageParser = new ServiceBusMessageParser(DefaultAgentId);

// AFTER:
var messageParser = new ServiceBusMessageParser([DefaultAgentId]);

// BEFORE (lines 142-150):
var responseHandler = new ServiceBusResponseHandler(
    promptReceiver,
    responseWriter,
    DefaultAgentId);

var client = new ServiceBusChatMessengerClient(
    promptReceiver,
    responseHandler,
    DefaultAgentId);

// AFTER:
var responseHandler = new ServiceBusResponseHandler(
    promptReceiver,
    responseWriter);

var client = new ServiceBusChatMessengerClient(
    promptReceiver,
    responseHandler);
```

**Success Criteria**:
- [ ] File compiles without errors
- [ ] Verification command: `dotnet build Tests/Tests.csproj`

**Dependencies**: All production Service Bus files updated
**Provides**: Test fixture for integration tests

---

### Tests/Integration/Messaging/ServiceBusIntegrationTests.cs [edit]

**Purpose**: Update integration tests to use new message format with correlationId.
**TOTAL CHANGES**: 5

**Changes**:
1. Rename `sourceId` variables to `correlationId` throughout (lines 38, 159, etc.)
2. Update `SendPromptAsync` calls to pass `correlationId` parameter (lines 44, 90, 163-164, 198-199)
3. Update response verification to check `correlationId` property (line 75)
4. Update test `SendPrompt_MissingSourceId_GeneratesUuidAndProcesses` to test missing correlationId behavior (dead-letter instead of generate)
5. Add test for invalid agentId dead-lettering

**Implementation Details**:
- All `sourceId` references become `correlationId`
- Missing correlationId now causes dead-letter instead of auto-generation
- Add test for invalid agentId validation

**Reference Implementation**:
```csharp
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
        // Arrange - Send JSON without correlationId
        const string missingCorrelationIdJson = """{"agentId": "test-agent", "prompt": "Hello", "sender": "user"}""";

        // Act
        await fixture.SendRawMessageAsync(missingCorrelationIdJson);

        // Wait for processing
        await Task.Delay(1000);

        // Assert - Message should be in dead-letter queue with MissingField
        var deadLetterMessages = await fixture.GetDeadLetterMessagesAsync();
        deadLetterMessages.ShouldNotBeEmpty();

        var dlMessage = deadLetterMessages.First();
        dlMessage.DeadLetterReason.ShouldBe("MissingField");
        dlMessage.DeadLetterErrorDescription.ShouldContain("correlationId");
    }

    [Fact]
    public async Task SendPrompt_InvalidAgentId_DeadLettered()
    {
        // Arrange - Send message with unknown agentId
        const string invalidAgentJson = """{"correlationId": "test-123", "agentId": "unknown-agent", "prompt": "Hello", "sender": "user"}""";

        // Act
        await fixture.SendRawMessageAsync(invalidAgentJson);

        // Wait for processing
        await Task.Delay(1000);

        // Assert - Message should be in dead-letter queue with InvalidAgentId
        var deadLetterMessages = await fixture.GetDeadLetterMessagesAsync();
        deadLetterMessages.ShouldNotBeEmpty();

        var dlMessage = deadLetterMessages.First();
        dlMessage.DeadLetterReason.ShouldBe("InvalidAgentId");
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

        // Assert - Message should be in dead-letter queue with MissingField
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
```

**Success Criteria**:
- [ ] All integration tests compile
- [ ] All integration tests pass
- [ ] Verification command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBusIntegrationTests"`

**Dependencies**: `Tests/Integration/Fixtures/ServiceBusFixture.cs`, all production Service Bus files
**Provides**: Integration test coverage for Service Bus message flow

---

## Dependency Graph

> Files in the same phase can execute in parallel.

| Phase | File | Action | Depends On |
|-------|------|--------|------------|
| 1 | `Domain/DTOs/ServiceBusPromptMessage.cs` | edit | — |
| 1 | `Domain/DTOs/ParsedServiceBusMessage.cs` | edit | — |
| 1 | `Domain/DTOs/ServiceBusResponseMessage.cs` | create | — |
| 2 | `Tests/Unit/Infrastructure/Messaging/ServiceBusMessageParserTests.cs` | edit | `Domain/DTOs/ServiceBusPromptMessage.cs`, `Domain/DTOs/ParsedServiceBusMessage.cs` |
| 2 | `Tests/Unit/Infrastructure/Messaging/ServiceBusSourceMapperTests.cs` | edit | `Domain/DTOs/ParsedServiceBusMessage.cs` |
| 2 | `Tests/Unit/Infrastructure/Messaging/ServiceBusResponseWriterTests.cs` | edit | `Domain/DTOs/ServiceBusResponseMessage.cs` |
| 3 | `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusMessageParser.cs` | edit | `Tests/Unit/Infrastructure/Messaging/ServiceBusMessageParserTests.cs` |
| 3 | `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusSourceMapper.cs` | edit | `Tests/Unit/Infrastructure/Messaging/ServiceBusSourceMapperTests.cs` |
| 3 | `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusResponseWriter.cs` | edit | `Tests/Unit/Infrastructure/Messaging/ServiceBusResponseWriterTests.cs` |
| 4 | `Tests/Unit/Infrastructure/Messaging/ServiceBusPromptReceiverTests.cs` | edit | `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusSourceMapper.cs`, `Domain/DTOs/ParsedServiceBusMessage.cs` |
| 5 | `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusPromptReceiver.cs` | edit | `Tests/Unit/Infrastructure/Messaging/ServiceBusPromptReceiverTests.cs` |
| 6 | `Tests/Unit/Infrastructure/Messaging/ServiceBusResponseHandlerTests.cs` | edit | `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusPromptReceiver.cs`, `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusResponseWriter.cs` |
| 7 | `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusResponseHandler.cs` | edit | `Tests/Unit/Infrastructure/Messaging/ServiceBusResponseHandlerTests.cs` |
| 8 | `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusChatMessengerClient.cs` | edit | `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusResponseHandler.cs` |
| 9 | `Agent/Modules/InjectorModule.cs` | edit | `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusMessageParser.cs`, `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusResponseHandler.cs`, `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusChatMessengerClient.cs` |
| 10 | `Tests/Integration/Fixtures/ServiceBusFixture.cs` | edit | All production Service Bus files |
| 11 | `Tests/Integration/Messaging/ServiceBusIntegrationTests.cs` | edit | `Tests/Integration/Fixtures/ServiceBusFixture.cs` |

## Exit Criteria

### Test Commands
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBus"
dotnet build
```

### Success Conditions
- [ ] All unit tests pass (exit code 0)
- [ ] All integration tests pass (exit code 0)
- [ ] Solution builds without errors (exit code 0)
- [ ] All requirements satisfied (R1-R10)
- [ ] All files implemented

### Verification Script
```bash
dotnet build && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ServiceBus"
```

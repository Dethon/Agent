# Service Bus Explicit Correlation ID and Agent ID

## Summary

Make `correlationId` and `agentId` **required fields** in Service Bus prompt messages. The `correlationId` serves as both the request/response correlation identifier and the conversation thread identifier for Redis persistence.

## Message Contracts

### Prompt Message (incoming)

```json
{
  "correlationId": "unique-request-id-123",
  "agentId": "jack",
  "prompt": "What movies are available?",
  "sender": "external-system"
}
```

All fields are **required**:
- `correlationId` — Unique identifier for request/response correlation AND conversation thread
- `agentId` — Must match a configured agent ID exactly (strict validation)
- `prompt` — The user's message
- `sender` — Identifier for the external caller

### Response Message (outgoing)

```json
{
  "correlationId": "unique-request-id-123",
  "agentId": "jack",
  "response": "Here are the available movies...",
  "completedAt": "2024-01-15T10:30:00Z"
}
```

The `sourceId` field is renamed to `correlationId` for consistency.

## Validation Behavior

| Condition | Result |
|-----------|--------|
| Missing `correlationId` | Dead-letter with reason "MissingField" |
| Missing `agentId` | Dead-letter with reason "MissingField" |
| Missing `prompt` | Dead-letter with reason "MissingField" |
| Unknown `agentId` | Dead-letter with reason "InvalidAgentId" |
| Valid message | Process normally |

## File Changes

### Domain DTOs

**Domain/DTOs/ServiceBusPromptMessage.cs**
```csharp
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

**Domain/DTOs/ParsedServiceBusMessage.cs**
```csharp
public sealed record ParsedServiceBusMessage(
    string CorrelationId,  // renamed from SourceId
    string AgentId,
    string Prompt,
    string Sender);
```

**New: Domain/DTOs/ServiceBusResponseMessage.cs**
```csharp
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

### Infrastructure Changes

**ServiceBusMessageParser** — Constructor takes `IReadOnlyList<string> validAgentIds`, validates all required fields from JSON body, strict agent validation.

**ServiceBusResponseWriter** — Rename `sourceId` parameter to `correlationId`, use public `ServiceBusResponseMessage` DTO.

**ServiceBusConversationMapper** — Rename:
- Parameter: `sourceId` → `correlationId`
- Redis key: `sb-source:{agentId}:{sourceId}` → `sb-correlation:{agentId}:{correlationId}`
- Dictionary: `_chatIdToSourceId` → `_chatIdToCorrelationId`
- Method: `TryGetSourceId` → `TryGetCorrelationId`

**ServiceBusResponseHandler** — Remove `defaultAgentId` parameter, rename internal references.

**ServiceBusPromptReceiver** — Rename `TryGetSourceId` → `TryGetCorrelationId`.

**ServiceBusChatMessengerClient** — Remove `defaultAgentId` parameter.

### DI Registration

**InjectorModule.AddServiceBusClient** — Accept `IReadOnlyList<AgentDefinition>` instead of `string defaultAgentId`, extract valid agent IDs for parser.

## Test Updates

| Test File | Changes |
|-----------|---------|
| `ServiceBusMessageParserTests.cs` | Test required field validation, remove optional property tests |
| `ServiceBusPromptReceiverTests.cs` | Rename `SourceId` → `CorrelationId` |
| `ServiceBusSourceMapperTests.cs` | Rename method calls |
| `ServiceBusResponseWriterTests.cs` | Rename `sourceId` → `correlationId` |
| `ServiceBusResponseHandlerTests.cs` | Remove `defaultAgentId` from constructor |
| `ServiceBusIntegrationTests.cs` | Update message format |
| `ServiceBusFixture.cs` | Update test message builders |

### New Test Scenarios

1. Missing `correlationId` → dead-letter with "MissingField"
2. Missing `agentId` → dead-letter with "MissingField"
3. Invalid `agentId` → dead-letter with "InvalidAgentId"
4. Valid message with known agent → success

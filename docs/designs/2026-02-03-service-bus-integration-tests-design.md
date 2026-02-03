# Azure Service Bus Integration Tests Design

## Overview

Add proper integration tests for the Azure Service Bus messaging components using Testcontainers with the Azure Service Bus emulator. Tests verify the full end-to-end flow: sending prompts, processing, and receiving responses.

## Test Infrastructure

### ServiceBusFixture

A test fixture implementing `IAsyncLifetime` that manages:

- **Azure Service Bus emulator container** via `Testcontainers.ServiceBus`
- **Redis container** via composed `RedisFixture` (for `ServiceBusSourceMapper`)
- **Queue creation** via `ServiceBusAdministrationClient`

```csharp
public class ServiceBusFixture : IAsyncLifetime
{
    private ServiceBusContainer _serviceBusContainer = null!;
    private RedisFixture _redisFixture = null!;

    public const string PromptQueueName = "agent-prompts";
    public const string ResponseQueueName = "agent-responses";

    public string ConnectionString { get; private set; } = null!;
    public IConnectionMultiplexer RedisConnection => _redisFixture.Connection;
}
```

### Emulator Configuration

File: `Tests/Integration/Fixtures/ServiceBusConfig.json`

```json
{
  "UserConfig": {
    "Namespaces": [{
      "Name": "test-namespace",
      "Queues": [
        { "Name": "agent-prompts" },
        { "Name": "agent-responses" }
      ]
    }]
  }
}
```

### Test Class Structure

```csharp
public class ServiceBusIntegrationTests(ServiceBusFixture fixture)
    : IClassFixture<ServiceBusFixture>, IAsyncLifetime
```

Tests use unique `sourceId` values (e.g., `$"test-{Guid.NewGuid():N}"`) for isolation within the shared fixture.

## Test Scenarios

### Happy Path

| Test | Description |
|------|-------------|
| `SendPrompt_ValidMessage_ProcessedAndResponseWritten` | Send well-formed message, verify processor receives it, messenger enqueues it, response appears on response queue |
| `SendPrompt_MissingSourceId_GeneratesUuidAndProcesses` | Message without `sourceId` property generates UUID and processing continues |

### Error Handling

| Test | Description |
|------|-------------|
| `SendPrompt_MalformedJson_DeadLettered` | Invalid JSON lands in dead-letter queue with reason "DeserializationError" |
| `SendPrompt_MissingPromptField_DeadLettered` | `{ "sender": "test" }` (no prompt) dead-lettered with reason "MalformedMessage" |

### Conversation Continuity

| Test | Description |
|------|-------------|
| `SendPrompt_SameSourceId_SameChatIdThreadId` | Two messages with same `sourceId` map to identical `chatId`/`threadId` |
| `SendPrompt_DifferentSourceIds_DifferentChatIds` | Different `sourceId` values get distinct `chatId`/`threadId` mappings |

## Fixture Helper Methods

```csharp
// Send a prompt to the queue
Task SendPromptAsync(string prompt, string sender, string? sourceId = null, string? agentId = null);

// Receive a response (with timeout)
Task<ServiceBusReceivedMessage?> ReceiveResponseAsync(TimeSpan timeout);

// Get dead-lettered messages
Task<IReadOnlyList<ServiceBusReceivedMessage>> GetDeadLetterMessagesAsync();

// Factory methods for creating components under test
ServiceBusChatMessengerClient CreateMessengerClient();
ServiceBusProcessorHost CreateProcessorHost(ServiceBusChatMessengerClient client);
```

## Implementation Files

### New Files

```
Tests/
├── Integration/
│   ├── Fixtures/
│   │   ├── ServiceBusFixture.cs           # Container + Redis fixture
│   │   └── ServiceBusConfig.json          # Emulator queue config
│   └── Messaging/
│       └── ServiceBusIntegrationTests.cs  # Integration test class
```

### Package Addition

Add to `Tests.csproj`:
```xml
<PackageReference Include="Testcontainers.ServiceBus" Version="4.10.0" />
```

## Test Execution Flow

1. **Fixture Init** (`InitializeAsync`):
   - Start Redis container
   - Start Service Bus emulator with config
   - Create `ServiceBusClient` and `ServiceBusAdministrationClient`

2. **Test Setup** (`InitializeAsync` per test class):
   - Create `ServiceBusSourceMapper` with Redis connection
   - Create `ServiceBusResponseWriter` with response queue sender
   - Create `ServiceBusChatMessengerClient`
   - Start `ServiceBusProcessorHost`

3. **Test Execution**:
   - Send message to prompt queue
   - Wait for response on response queue (or dead-letter queue for error cases)
   - Assert expected behavior

4. **Cleanup** (`DisposeAsync`):
   - Stop processor host
   - Dispose clients
   - Stop containers

## Test Isolation Strategy

Each test generates a unique `sourceId` using `Guid.NewGuid().ToString("N")`. This ensures:

- Tests can run in parallel without interference
- Same fixture instance can be reused across all tests in a class
- No need to clear queues between tests

## Dependencies

- `Testcontainers.ServiceBus` - Azure Service Bus emulator container
- `Azure.Messaging.ServiceBus` - Service Bus SDK (already in Infrastructure)
- Existing `RedisFixture` - Redis container for source mapping

## References

- [Testcontainers Azure Service Bus Module](https://dotnet.testcontainers.org/modules/servicebus/)
- [Azure Service Bus Emulator Overview](https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator)

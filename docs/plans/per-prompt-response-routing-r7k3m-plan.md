# Per-Prompt Response Routing Implementation Plan

## Summary

Implement per-prompt response routing in `CompositeChatMessengerClient` to ensure responses are routed based on prompt origin. Currently all responses broadcast to all clients; after implementation, WebUI receives all responses (universal viewer), while ServiceBus and Telegram only receive responses for prompts they originated.

## Files

> **Note**: This is the canonical file list.

### Files to Create
- `Domain/DTOs/MessageSource.cs`
- `Tests/Unit/Infrastructure/Messaging/MessageSourceRoutingTests.cs`

### Files to Edit
- `Domain/Contracts/IChatMessengerClient.cs`
- `Domain/DTOs/ChatPrompt.cs`
- `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs`
- `Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs`
- `Infrastructure/Clients/Messaging/TelegramChatClient.cs`
- `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs`
- `Infrastructure/Clients/Messaging/CliChatMessengerClient.cs`
- `Infrastructure/Clients/Messaging/OneShotChatMessengerClient.cs`
- `Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs`

## Code Context

### Interface: `IChatMessengerClient` (Domain/Contracts/IChatMessengerClient.cs)
- Lines 7-25: Interface defining messenger client contract
- Line 9: `bool SupportsScheduledNotifications { get; }` - existing property pattern to follow
- Line 11: `IAsyncEnumerable<ChatPrompt> ReadPrompts(...)` - returns prompts from clients
- Lines 13-14: `Task ProcessResponseStreamAsync(...)` - receives updates to broadcast

### DTO: `ChatPrompt` (Domain/DTOs/ChatPrompt.cs)
- Lines 5-14: Record definition with required properties
- Line 8: `public required string Prompt { get; init; }` - pattern for new Source property
- Currently no Source property exists

### Composite Client: `CompositeChatMessengerClient` (Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs)
- Lines 10-11: Class definition with `IReadOnlyList<IChatMessengerClient> clients` dependency
- Lines 15-21: `ReadPrompts` method merges prompts from all clients using `Merge` extension
- Lines 23-43: `ProcessResponseStreamAsync` broadcasts to all clients indiscriminately
- Lines 28-30: Creates channels per client
- Lines 81-100: `BroadcastUpdatesAsync` writes to ALL channels (current bug)
- Line 90: `await Task.WhenAll(channels.Select(c => c.Writer.WriteAsync(update, ct).AsTask()))` - broadcasts to all

### ServiceBus Client: `ServiceBusChatMessengerClient` (Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs)
- Lines 14-18: Primary constructor with dependencies
- Line 25: `public bool SupportsScheduledNotifications => false;` - property pattern
- Lines 102-132: `EnqueueReceivedMessageAsync` creates `ChatPrompt` at lines 117-125

### Telegram Client: `TelegramChatClient` (Infrastructure/Clients/Messaging/TelegramChatClient.cs)
- Lines 16-21: Primary constructor
- Line 30: `public bool SupportsScheduledNotifications => false;`
- Lines 296-313: `GetPromptFromUpdate` creates `ChatPrompt` without Source

### WebChat Client: `WebChatMessengerClient` (Infrastructure/Clients/Messaging/WebChatMessengerClient.cs)
- Lines 16-23: Primary constructor
- Line 29: `public bool SupportsScheduledNotifications => true;`
- Lines 273-281: Creates `ChatPrompt` in `EnqueuePromptAndGetResponses`
- Lines 320-331: Creates `ChatPrompt` in `EnqueuePrompt`

### CLI Client: `CliChatMessengerClient` (Infrastructure/Clients/Messaging/CliChatMessengerClient.cs)
- Lines 12-32: Class definition and constructor
- Line 18: `public bool SupportsScheduledNotifications => false;`

### OneShot Client: `OneShotChatMessengerClient` (Infrastructure/Clients/Messaging/OneShotChatMessengerClient.cs)
- Lines 11-14: Primary constructor
- Line 22: `public bool SupportsScheduledNotifications => false;`
- Lines 34-41: Creates `ChatPrompt` without Source

### Existing Tests: `CompositeChatMessengerClientTests` (Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs)
- Lines 100-149: `ProcessResponseStreamAsync_BroadcastsToAllClients` - currently tests broadcast to ALL
- Line 18: Mock setup pattern for `IChatMessengerClient`

### Extension: `IAsyncEnumerableExtensions` (Domain/Extensions/IAsyncEnumerableExtensions.cs)
- Lines 127-148: `Merge` extension used by CompositeChatMessengerClient

### DI Registration: `InjectorModule.cs` (Agent/Modules/InjectorModule.cs)
- Lines 201-205: `CompositeChatMessengerClient` created with WebChat and ServiceBus clients

## External Context

N/A - This is internal routing logic using standard .NET types (`ConcurrentDictionary`, `Channel`).

## Architectural Narrative

### Task

Implement per-prompt response routing so that:
1. Track the source of each prompt (WebUI, ServiceBus, Telegram, CLI)
2. Route responses based on prompt origin:
   - WebUI: Receives ALL responses (universal viewer)
   - ServiceBus: Only receives responses for ServiceBus-originated prompts
   - Telegram: Only receives responses for Telegram-originated prompts
   - CLI/OneShot: Only receives responses for CLI-originated prompts

### Architecture

The messaging architecture follows a composite pattern:
- `IChatMessengerClient` defines the contract for all messenger clients
- `CompositeChatMessengerClient` aggregates multiple clients, merging their prompt streams and broadcasting responses
- Each concrete client (Telegram, ServiceBus, WebChat, CLI, OneShot) implements the interface

Current flow:
1. Prompts come from various clients via `ReadPrompts()`
2. `CompositeChatMessengerClient.ReadPrompts()` merges all prompt streams
3. Responses flow through `ProcessResponseStreamAsync()`
4. `BroadcastUpdatesAsync()` writes to ALL client channels (the bug)

Target flow:
1. Prompts come with `Source` property set by originating client
2. `CompositeChatMessengerClient` tracks `ChatId -> MessageSource` mapping
3. `BroadcastUpdatesAsync()` filters updates per client based on source

### Selected Context

| File | Provides |
|------|----------|
| `Domain/DTOs/MessageSource.cs` | `MessageSource` enum (WebUi, ServiceBus, Telegram, Cli) |
| `Domain/Contracts/IChatMessengerClient.cs` | `Source` property on interface |
| `Domain/DTOs/ChatPrompt.cs` | `Source` property on DTO |
| `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs` | Routing logic with source tracking |

### Relationships

```
ChatPrompt (with Source)
    ↓ ReadPrompts()
CompositeChatMessengerClient
    ↓ tracks ChatId -> Source
    ↓ ProcessResponseStreamAsync()
BroadcastUpdatesAsync
    ↓ filters by client.Source vs prompt Source
Individual clients (filtered)
```

### External Context

N/A

### Implementation Notes

1. **Source tracking scope**: Track at `ChatId` level, not `AgentKey`. A single chat may switch sources between prompts, but for routing we care about the most recent prompt's source for that ChatId.

2. **Default source handling**: When a ChatId has no tracked source (edge case), default behavior should be WebUI-like (receive the update) to avoid dropping messages.

3. **Thread safety**: Use `ConcurrentDictionary<long, MessageSource>` for thread-safe source tracking.

4. **CLI/OneShot handling**: These are single-user clients that always receive their own responses. Add `MessageSource.Cli` to cover both.

5. **Existing mock compatibility**: Tests use `Mock<IChatMessengerClient>` - ensure `Source` property has a sensible default or is set in test setup.

### Ambiguities

- **Design decision**: CLI and OneShot clients share `MessageSource.Cli` since they're both local terminal interfaces and routing behavior is identical.
- **Design decision**: Default source when unknown is treated as WebUI to avoid dropping messages silently.

### Requirements

1. WebUI receives ALL responses regardless of prompt origin
2. ServiceBus receives ONLY responses for ServiceBus-originated prompts
3. Telegram receives ONLY responses for Telegram-originated prompts
4. CLI/OneShot receive ONLY responses for CLI-originated prompts
5. A chat session may receive prompts from multiple sources; each response routes per its prompt's origin
6. No response is dropped for unknown ChatIds (fail-safe to broadcast)

### Constraints

- No breaking changes to `IChatMessengerClient` interface (new property with default implementation)
- `ChatPrompt` must remain backward compatible (new property should have default)
- Thread-safe source tracking required due to concurrent prompt/response processing
- Follow existing patterns: file-scoped namespaces, primary constructors, `record` for DTOs

### Selected Approach

**Approach**: ChatId-to-Source Dictionary with Client Source Property
**Description**: Add `MessageSource Source { get; }` property to `IChatMessengerClient`. Each concrete client returns its type. Add `Source` property to `ChatPrompt`. `CompositeChatMessengerClient` tracks `ConcurrentDictionary<long, MessageSource>` mapping ChatId to most recent prompt source. In `BroadcastUpdatesAsync`, filter updates to only write to clients where `client.Source == MessageSource.WebUi || client.Source == promptSource`.
**Rationale**: This approach minimizes changes to existing code, uses the existing interface pattern (`SupportsScheduledNotifications`), and keeps routing logic centralized in the composite client.
**Trade-offs Accepted**: Source tracking is per-ChatId not per-message; if the same ChatId receives prompts from different sources in quick succession, the most recent wins. This matches the design document's intent.

## Implementation Plan

### Domain/DTOs/MessageSource.cs [create]

**Purpose**: Define the enum representing message source origins for routing decisions.

**TOTAL CHANGES**: 1

**Changes**:
1. Create new enum file with WebUi, ServiceBus, Telegram, Cli values

**Implementation Details**:
- Namespace: `Domain.DTOs`
- File-scoped namespace per project conventions
- Simple enum with four values
- `PublicAPI` attribute for JetBrains annotations

**Reference Implementation**:
```csharp
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public enum MessageSource
{
    WebUi,
    ServiceBus,
    Telegram,
    Cli
}
```

**Dependencies**: None
**Provides**: `MessageSource` enum type

---

### Domain/Contracts/IChatMessengerClient.cs [edit]

**Purpose**: Add `Source` property to the interface so composite client can identify each client's type.

**TOTAL CHANGES**: 2

**Changes**:
1. Add `using Domain.DTOs;` import at line 2 (after existing `using Domain.Agents;`)
2. Add `MessageSource Source { get; }` property after line 9 (after `SupportsScheduledNotifications`)

**Implementation Details**:
- New property follows same pattern as `SupportsScheduledNotifications`
- No default implementation in interface (concrete classes must implement)

**Reference Implementation**:
```csharp
using Domain.Agents;
using Domain.DTOs;
using Microsoft.Agents.AI;

namespace Domain.Contracts;

public interface IChatMessengerClient
{
    bool SupportsScheduledNotifications { get; }

    MessageSource Source { get; }

    IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken);

    Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates, CancellationToken cancellationToken);

    Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId, CancellationToken cancellationToken);

    Task<AgentKey> CreateTopicIfNeededAsync(
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default);

    Task StartScheduledStreamAsync(AgentKey agentKey, CancellationToken ct = default);
}
```

**Migration Pattern**:
```csharp
// BEFORE (line 9):
    bool SupportsScheduledNotifications { get; }

// AFTER (lines 9-11):
    bool SupportsScheduledNotifications { get; }

    MessageSource Source { get; }
```

**Dependencies**: `Domain/DTOs/MessageSource.cs`
**Provides**: `IChatMessengerClient.Source` property signature

---

### Domain/DTOs/ChatPrompt.cs [edit]

**Purpose**: Add `Source` property to track which client originated each prompt.

**TOTAL CHANGES**: 1

**Changes**:
1. Add `public MessageSource Source { get; init; }` property after line 13 (after `AgentId`)

**Implementation Details**:
- Non-required property with default value (backward compatible)
- Defaults to `MessageSource.WebUi` for backward compatibility

**Reference Implementation**:
```csharp
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record ChatPrompt
{
    public required string Prompt { get; init; }
    public required long ChatId { get; init; }
    public required int? ThreadId { get; init; }
    public required int MessageId { get; init; }
    public required string Sender { get; init; }
    public string? AgentId { get; init; }
    public MessageSource Source { get; init; } = MessageSource.WebUi;
}
```

**Migration Pattern**:
```csharp
// BEFORE (line 13):
    public string? AgentId { get; init; }
}

// AFTER (lines 13-14):
    public string? AgentId { get; init; }
    public MessageSource Source { get; init; } = MessageSource.WebUi;
}
```

**Dependencies**: `Domain/DTOs/MessageSource.cs`
**Provides**: `ChatPrompt.Source` property

---

### Tests/Unit/Infrastructure/Messaging/MessageSourceRoutingTests.cs [create]

**Purpose**: Test routing logic for the CompositeChatMessengerClient with source-based filtering.

**TOTAL CHANGES**: 1

**Changes**:
1. Create comprehensive test class for source-based routing scenarios

**Implementation Details**:
- Test WebUI receives all responses
- Test ServiceBus only receives ServiceBus-originated responses
- Test Telegram only receives Telegram-originated responses
- Test unknown ChatId defaults to broadcasting (fail-safe)
- Use Moq for mocking `IChatMessengerClient`

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

public class MessageSourceRoutingTests
{
    [Fact]
    public async Task ProcessResponseStreamAsync_WebUiClientReceivesAllResponses()
    {
        // Arrange
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();
        var serviceBusUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus, serviceBusUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, serviceBusClient.Object]);

        // Simulate reading a prompt from ServiceBus to establish source mapping
        var serviceBusPrompt = new ChatPrompt
        {
            Prompt = "Hello from ServiceBus",
            ChatId = 123,
            ThreadId = 1,
            MessageId = 1,
            Sender = "user",
            Source = MessageSource.ServiceBus
        };

        serviceBusClient.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { serviceBusPrompt }.ToAsyncEnumerable());
        webUiClient.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<ChatPrompt>());

        // Read prompts to establish source tracking
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var _ in composite.ReadPrompts(100, cts.Token).Take(1)) { }

        // Create response for the ServiceBus prompt
        var response = (
            new AgentKey(123, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Response")] },
            (AiResponse?)null);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { response }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert - WebUI should receive the response (universal viewer)
        webUiUpdates.Count.ShouldBe(1);
        // ServiceBus should also receive it (source matches)
        serviceBusUpdates.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_ServiceBusClientDoesNotReceiveWebUiResponses()
    {
        // Arrange
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();
        var serviceBusUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus, serviceBusUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, serviceBusClient.Object]);

        // Simulate reading a prompt from WebUI
        var webUiPrompt = new ChatPrompt
        {
            Prompt = "Hello from WebUI",
            ChatId = 456,
            ThreadId = 1,
            MessageId = 1,
            Sender = "user",
            Source = MessageSource.WebUi
        };

        webUiClient.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { webUiPrompt }.ToAsyncEnumerable());
        serviceBusClient.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<ChatPrompt>());

        // Read prompts to establish source tracking
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var _ in composite.ReadPrompts(100, cts.Token).Take(1)) { }

        // Create response for the WebUI prompt
        var response = (
            new AgentKey(456, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Response")] },
            (AiResponse?)null);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { response }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert - WebUI should receive the response
        webUiUpdates.Count.ShouldBe(1);
        // ServiceBus should NOT receive it (source doesn't match and it's not WebUI)
        serviceBusUpdates.Count.ShouldBe(0);
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_UnknownChatIdBroadcastsToAll()
    {
        // Arrange
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();
        var serviceBusUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus, serviceBusUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, serviceBusClient.Object]);

        // Don't read any prompts - ChatId 789 will be unknown

        // Create response for unknown ChatId
        var response = (
            new AgentKey(789, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Response")] },
            (AiResponse?)null);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { response }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert - Both should receive it as fail-safe (unknown source defaults to broadcast)
        webUiUpdates.Count.ShouldBe(1);
        serviceBusUpdates.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_TelegramClientOnlyReceivesTelegramResponses()
    {
        // Arrange
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();
        var telegramUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var telegramClient = CreateMockClient(MessageSource.Telegram, telegramUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, telegramClient.Object]);

        // Simulate reading a prompt from Telegram
        var telegramPrompt = new ChatPrompt
        {
            Prompt = "Hello from Telegram",
            ChatId = 100,
            ThreadId = 1,
            MessageId = 1,
            Sender = "user",
            Source = MessageSource.Telegram
        };

        telegramClient.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { telegramPrompt }.ToAsyncEnumerable());
        webUiClient.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<ChatPrompt>());

        // Read prompts to establish source tracking
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var _ in composite.ReadPrompts(100, cts.Token).Take(1)) { }

        // Create response for the Telegram prompt
        var response = (
            new AgentKey(100, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Response")] },
            (AiResponse?)null);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { response }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert
        webUiUpdates.Count.ShouldBe(1);  // WebUI receives all
        telegramUpdates.Count.ShouldBe(1);  // Telegram receives its own
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_SameChatIdDifferentSources_UsesLatestSource()
    {
        // Arrange
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();
        var serviceBusUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus, serviceBusUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, serviceBusClient.Object]);

        // First prompt from ServiceBus
        var serviceBusPrompt = new ChatPrompt
        {
            Prompt = "First from ServiceBus",
            ChatId = 200,
            ThreadId = 1,
            MessageId = 1,
            Sender = "user",
            Source = MessageSource.ServiceBus
        };

        // Second prompt from WebUI with SAME ChatId
        var webUiPrompt = new ChatPrompt
        {
            Prompt = "Second from WebUI",
            ChatId = 200,
            ThreadId = 1,
            MessageId = 2,
            Sender = "user",
            Source = MessageSource.WebUi
        };

        serviceBusClient.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { serviceBusPrompt }.ToAsyncEnumerable());
        webUiClient.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { webUiPrompt }.ToAsyncEnumerable());

        // Read prompts (ServiceBus first, then WebUI - WebUI overwrites source)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var _ in composite.ReadPrompts(100, cts.Token).Take(2)) { }

        // Create response for ChatId 200
        var response = (
            new AgentKey(200, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Response")] },
            (AiResponse?)null);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { response }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert - Latest source was WebUI, so ServiceBus should NOT receive
        webUiUpdates.Count.ShouldBe(1);
        serviceBusUpdates.Count.ShouldBe(0);
    }

    private static Mock<IChatMessengerClient> CreateMockClient(
        MessageSource source,
        List<(AgentKey, AgentResponseUpdate, AiResponse?)> receivedUpdates)
    {
        var mock = new Mock<IChatMessengerClient>();
        mock.Setup(c => c.Source).Returns(source);
        mock.Setup(c => c.SupportsScheduledNotifications).Returns(false);
        mock.Setup(c => c.ProcessResponseStreamAsync(
                It.IsAny<IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
                CancellationToken ct) =>
            {
                await foreach (var update in updates.WithCancellation(ct))
                {
                    receivedUpdates.Add(update);
                }
            });
        return mock;
    }
}
```

**Dependencies**: `Domain/DTOs/MessageSource.cs`, `Domain/Contracts/IChatMessengerClient.cs`, `Domain/DTOs/ChatPrompt.cs`
**Provides**: Test coverage for routing logic

---

### Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs [edit]

**Purpose**: Implement source tracking and filtered routing in the composite client.

**TOTAL CHANGES**: 5

**Changes**:
1. Add `using System.Collections.Concurrent;` import at line 1
2. Add `Source` property after line 13 (returns `MessageSource.WebUi` as composite acts as universal viewer)
3. Add `_chatIdToSource` dictionary field after line 12
4. Modify `ReadPrompts` method (lines 15-21) to track source mapping when yielding prompts
5. Modify `BroadcastUpdatesAsync` method (lines 81-100) to filter updates based on source

**Implementation Details**:
- `ConcurrentDictionary<long, MessageSource> _chatIdToSource` for thread-safe tracking
- In `ReadPrompts`: after yielding each prompt, store `_chatIdToSource[prompt.ChatId] = prompt.Source`
- In `BroadcastUpdatesAsync`: for each update, get `promptSource` from dictionary
- Filter: write to client channel only if `client.Source == MessageSource.WebUi || client.Source == promptSource`
- For unknown ChatId (not in dictionary), default to `MessageSource.WebUi` (broadcasts to all)

**Reference Implementation**:
```csharp
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<long, MessageSource> _chatIdToSource = new();

    public bool SupportsScheduledNotifications => clients.Any(c => c.SupportsScheduledNotifications);

    public MessageSource Source => MessageSource.WebUi;

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Validate();
        await foreach (var prompt in clients
            .Select(c => c.ReadPrompts(timeout, cancellationToken))
            .Merge(cancellationToken))
        {
            _chatIdToSource[prompt.ChatId] = prompt.Source;
            yield return prompt;
        }
    }

    public async Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
        CancellationToken cancellationToken)
    {
        Validate();
        var clientChannels = clients
            .Select(client => (
                client,
                channel: Channel.CreateUnbounded<(AgentKey, AgentResponseUpdate, AiResponse?)>()))
            .ToArray();

        var broadcastTask = BroadcastUpdatesAsync(updates, clientChannels, cancellationToken);

        var processTasks = clientChannels
            .Select(pair => pair.client.ProcessResponseStreamAsync(
                pair.channel.channel.Reader.ReadAllAsync(cancellationToken),
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
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default)
    {
        Validate();
        var agentKeys = clients.Select(x => x.CreateTopicIfNeededAsync(chatId, threadId, agentId, topicName, ct));
        return (await Task.WhenAll(agentKeys)).First();
    }

    public async Task StartScheduledStreamAsync(AgentKey agentKey, CancellationToken ct = default)
    {
        Validate();
        await Task.WhenAll(clients.Select(c => c.StartScheduledStreamAsync(agentKey, ct)));
    }

    private void Validate()
    {
        if (clients.Count == 0)
        {
            throw new InvalidOperationException($"{nameof(clients)} must contain at least one client");
        }
    }

    private async Task BroadcastUpdatesAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> source,
        (IChatMessengerClient client, Channel<(AgentKey, AgentResponseUpdate, AiResponse?)> channel)[] clientChannels,
        CancellationToken ct)
    {
        try
        {
            await foreach (var update in source.WithCancellation(ct))
            {
                var (agentKey, _, _) = update;
                var promptSource = _chatIdToSource.GetValueOrDefault(agentKey.ChatId, MessageSource.WebUi);

                foreach (var (client, channel) in clientChannels)
                {
                    if (client.Source == MessageSource.WebUi || client.Source == promptSource)
                    {
                        await channel.Writer.WriteAsync(update, ct);
                    }
                }
            }
        }
        finally
        {
            foreach (var (_, channel) in clientChannels)
            {
                channel.Writer.TryComplete();
            }
        }
    }
}
```

**Migration Pattern**:
```csharp
// BEFORE (lines 10-11):
public sealed class CompositeChatMessengerClient(
    IReadOnlyList<IChatMessengerClient> clients) : IChatMessengerClient
{
    public bool SupportsScheduledNotifications => clients.Any(c => c.SupportsScheduledNotifications);

// AFTER:
public sealed class CompositeChatMessengerClient(
    IReadOnlyList<IChatMessengerClient> clients) : IChatMessengerClient
{
    private readonly ConcurrentDictionary<long, MessageSource> _chatIdToSource = new();

    public bool SupportsScheduledNotifications => clients.Any(c => c.SupportsScheduledNotifications);

    public MessageSource Source => MessageSource.WebUi;
```

```csharp
// BEFORE ReadPrompts (lines 15-21):
    public IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken)
    {
        Validate();
        return clients
            .Select(c => c.ReadPrompts(timeout, cancellationToken))
            .Merge(cancellationToken);
    }

// AFTER ReadPrompts:
    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Validate();
        await foreach (var prompt in clients
            .Select(c => c.ReadPrompts(timeout, cancellationToken))
            .Merge(cancellationToken))
        {
            _chatIdToSource[prompt.ChatId] = prompt.Source;
            yield return prompt;
        }
    }
```

```csharp
// BEFORE BroadcastUpdatesAsync (lines 81-100):
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

// AFTER BroadcastUpdatesAsync:
    private async Task BroadcastUpdatesAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> source,
        (IChatMessengerClient client, Channel<(AgentKey, AgentResponseUpdate, AiResponse?)> channel)[] clientChannels,
        CancellationToken ct)
    {
        try
        {
            await foreach (var update in source.WithCancellation(ct))
            {
                var (agentKey, _, _) = update;
                var promptSource = _chatIdToSource.GetValueOrDefault(agentKey.ChatId, MessageSource.WebUi);

                foreach (var (client, channel) in clientChannels)
                {
                    if (client.Source == MessageSource.WebUi || client.Source == promptSource)
                    {
                        await channel.Writer.WriteAsync(update, ct);
                    }
                }
            }
        }
        finally
        {
            foreach (var (_, channel) in clientChannels)
            {
                channel.Writer.TryComplete();
            }
        }
    }
```

**Dependencies**: `Domain/DTOs/MessageSource.cs`, `Domain/Contracts/IChatMessengerClient.cs`
**Provides**: Source-filtered response routing

---

### Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs [edit]

**Purpose**: Implement `Source` property and set source on created prompts.

**TOTAL CHANGES**: 2

**Changes**:
1. Add `public MessageSource Source => MessageSource.ServiceBus;` property after line 25 (after `SupportsScheduledNotifications`)
2. Add `Source = MessageSource.ServiceBus` to `ChatPrompt` creation at line 124

**Implementation Details**:
- Property returns constant `MessageSource.ServiceBus`
- ChatPrompt in `EnqueueReceivedMessageAsync` includes Source property

**Reference Implementation** (property addition):
```csharp
    public bool SupportsScheduledNotifications => false;

    public MessageSource Source => MessageSource.ServiceBus;
```

**Reference Implementation** (ChatPrompt modification at line 117-125):
```csharp
        var chatPrompt = new ChatPrompt
        {
            Prompt = prompt,
            ChatId = chatId,
            ThreadId = (int)threadId,
            MessageId = messageId,
            Sender = sender,
            AgentId = actualAgentId,
            Source = MessageSource.ServiceBus
        };
```

**Migration Pattern**:
```csharp
// BEFORE (line 25):
    public bool SupportsScheduledNotifications => false;

// AFTER (lines 25-27):
    public bool SupportsScheduledNotifications => false;

    public MessageSource Source => MessageSource.ServiceBus;
```

```csharp
// BEFORE (lines 117-125):
        var chatPrompt = new ChatPrompt
        {
            Prompt = prompt,
            ChatId = chatId,
            ThreadId = (int)threadId,
            MessageId = messageId,
            Sender = sender,
            AgentId = actualAgentId
        };

// AFTER (lines 117-126):
        var chatPrompt = new ChatPrompt
        {
            Prompt = prompt,
            ChatId = chatId,
            ThreadId = (int)threadId,
            MessageId = messageId,
            Sender = sender,
            AgentId = actualAgentId,
            Source = MessageSource.ServiceBus
        };
```

**Dependencies**: `Domain/DTOs/MessageSource.cs`, `Domain/Contracts/IChatMessengerClient.cs`
**Provides**: `ServiceBusChatMessengerClient.Source` property

---

### Infrastructure/Clients/Messaging/TelegramChatClient.cs [edit]

**Purpose**: Implement `Source` property and set source on created prompts.

**TOTAL CHANGES**: 2

**Changes**:
1. Add `public MessageSource Source => MessageSource.Telegram;` property after line 30 (after `SupportsScheduledNotifications`)
2. Add `Source = MessageSource.Telegram` to `ChatPrompt` creation at line 303-313

**Implementation Details**:
- Property returns constant `MessageSource.Telegram`
- ChatPrompt in `GetPromptFromUpdate` includes Source property

**Reference Implementation** (property addition):
```csharp
    public bool SupportsScheduledNotifications => false;

    public MessageSource Source => MessageSource.Telegram;
```

**Reference Implementation** (GetPromptFromUpdate modification at lines 296-313):
```csharp
    private static ChatPrompt GetPromptFromUpdate(Message message)
    {
        if (message.Text is null)
        {
            throw new ArgumentException(nameof(message.Text));
        }

        return new ChatPrompt
        {
            Prompt = message.Text,
            ChatId = message.Chat.Id,
            MessageId = message.MessageId,
            Sender = message.From?.Username ??
                     message.Chat.Username ??
                     message.Chat.FirstName ??
                     $"{message.Chat.Id}",
            ThreadId = message.MessageThreadId,
            Source = MessageSource.Telegram
        };
    }
```

**Migration Pattern**:
```csharp
// BEFORE (line 30):
    public bool SupportsScheduledNotifications => false;

// AFTER (lines 30-32):
    public bool SupportsScheduledNotifications => false;

    public MessageSource Source => MessageSource.Telegram;
```

```csharp
// BEFORE (lines 303-313):
        return new ChatPrompt
        {
            Prompt = message.Text,
            ChatId = message.Chat.Id,
            MessageId = message.MessageId,
            Sender = message.From?.Username ??
                     message.Chat.Username ??
                     message.Chat.FirstName ??
                     $"{message.Chat.Id}",
            ThreadId = message.MessageThreadId
        };

// AFTER:
        return new ChatPrompt
        {
            Prompt = message.Text,
            ChatId = message.Chat.Id,
            MessageId = message.MessageId,
            Sender = message.From?.Username ??
                     message.Chat.Username ??
                     message.Chat.FirstName ??
                     $"{message.Chat.Id}",
            ThreadId = message.MessageThreadId,
            Source = MessageSource.Telegram
        };
```

**Dependencies**: `Domain/DTOs/MessageSource.cs`, `Domain/Contracts/IChatMessengerClient.cs`
**Provides**: `TelegramChatClient.Source` property

---

### Infrastructure/Clients/Messaging/WebChatMessengerClient.cs [edit]

**Purpose**: Implement `Source` property and set source on created prompts.

**TOTAL CHANGES**: 3

**Changes**:
1. Add `public MessageSource Source => MessageSource.WebUi;` property after line 29 (after `SupportsScheduledNotifications`)
2. Add `Source = MessageSource.WebUi` to `ChatPrompt` creation at lines 273-281 (in `EnqueuePromptAndGetResponses`)
3. Add `Source = MessageSource.WebUi` to `ChatPrompt` creation at lines 320-331 (in `EnqueuePrompt`)

**Implementation Details**:
- Property returns constant `MessageSource.WebUi`
- Both ChatPrompt creation sites include Source property

**Reference Implementation** (property addition):
```csharp
    public bool SupportsScheduledNotifications => true;

    public MessageSource Source => MessageSource.WebUi;
```

**Reference Implementation** (EnqueuePromptAndGetResponses ChatPrompt at lines 273-281):
```csharp
        var prompt = new ChatPrompt
        {
            Prompt = message,
            ChatId = session.ChatId,
            ThreadId = (int)session.ThreadId,
            MessageId = messageId,
            Sender = sender,
            AgentId = session.AgentId,
            Source = MessageSource.WebUi
        };
```

**Reference Implementation** (EnqueuePrompt ChatPrompt at lines 320-331):
```csharp
        var prompt = new ChatPrompt
        {
            Prompt = message,
            ChatId = session.ChatId,
            ThreadId = (int)session.ThreadId,
            MessageId = messageId,
            Sender = sender,
            AgentId = session.AgentId,
            Source = MessageSource.WebUi
        };
```

**Migration Pattern**:
```csharp
// BEFORE (line 29):
    public bool SupportsScheduledNotifications => true;

// AFTER (lines 29-31):
    public bool SupportsScheduledNotifications => true;

    public MessageSource Source => MessageSource.WebUi;
```

**Dependencies**: `Domain/DTOs/MessageSource.cs`, `Domain/Contracts/IChatMessengerClient.cs`
**Provides**: `WebChatMessengerClient.Source` property

---

### Infrastructure/Clients/Messaging/CliChatMessengerClient.cs [edit]

**Purpose**: Implement `Source` property for CLI client.

**TOTAL CHANGES**: 1

**Changes**:
1. Add `public MessageSource Source => MessageSource.Cli;` property after line 18 (after `SupportsScheduledNotifications`)

**Implementation Details**:
- Property returns constant `MessageSource.Cli`
- CLI prompts come from the router which doesn't create ChatPrompt directly in this class

**Reference Implementation**:
```csharp
    public bool SupportsScheduledNotifications => false;

    public MessageSource Source => MessageSource.Cli;
```

**Migration Pattern**:
```csharp
// BEFORE (line 18):
    public bool SupportsScheduledNotifications => false;

// AFTER (lines 18-20):
    public bool SupportsScheduledNotifications => false;

    public MessageSource Source => MessageSource.Cli;
```

**Dependencies**: `Domain/DTOs/MessageSource.cs`, `Domain/Contracts/IChatMessengerClient.cs`
**Provides**: `CliChatMessengerClient.Source` property

---

### Infrastructure/Clients/Messaging/OneShotChatMessengerClient.cs [edit]

**Purpose**: Implement `Source` property and set source on created prompts.

**TOTAL CHANGES**: 2

**Changes**:
1. Add `public MessageSource Source => MessageSource.Cli;` property after line 22 (after `SupportsScheduledNotifications`)
2. Add `Source = MessageSource.Cli` to `ChatPrompt` creation at lines 34-41

**Implementation Details**:
- Property returns constant `MessageSource.Cli` (same as CLI since it's also a terminal interface)
- ChatPrompt creation includes Source property

**Reference Implementation** (property addition):
```csharp
    public bool SupportsScheduledNotifications => false;

    public MessageSource Source => MessageSource.Cli;
```

**Reference Implementation** (ReadPrompts ChatPrompt at lines 34-41):
```csharp
        yield return new ChatPrompt
        {
            Prompt = prompt,
            ChatId = 1,
            ThreadId = 1,
            MessageId = 1,
            Sender = Environment.UserName,
            Source = MessageSource.Cli
        };
```

**Migration Pattern**:
```csharp
// BEFORE (line 22):
    public bool SupportsScheduledNotifications => false;

// AFTER (lines 22-24):
    public bool SupportsScheduledNotifications => false;

    public MessageSource Source => MessageSource.Cli;
```

```csharp
// BEFORE (lines 34-41):
        yield return new ChatPrompt
        {
            Prompt = prompt,
            ChatId = 1,
            ThreadId = 1,
            MessageId = 1,
            Sender = Environment.UserName
        };

// AFTER:
        yield return new ChatPrompt
        {
            Prompt = prompt,
            ChatId = 1,
            ThreadId = 1,
            MessageId = 1,
            Sender = Environment.UserName,
            Source = MessageSource.Cli
        };
```

**Dependencies**: `Domain/DTOs/MessageSource.cs`, `Domain/Contracts/IChatMessengerClient.cs`
**Provides**: `OneShotChatMessengerClient.Source` property

---

### Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs [edit]

**Purpose**: Update existing tests to set up `Source` property on mock clients.

**TOTAL CHANGES**: 6

**Changes**:
1. Add `using Domain.DTOs;` import after line 3
2. Add `Source` setup to mock in `SupportsScheduledNotifications_WhenAnyClientSupports_ReturnsTrue` (lines 18-19)
3. Add `Source` setup to mock in `SupportsScheduledNotifications_WhenNoClientSupports_ReturnsFalse` (lines 34-35)
4. Add `Source` setup to mock in `ReadPrompts_MergesPromptsFromAllClients` (lines 68-69)
5. Add `Source` setup to mock in `ProcessResponseStreamAsync_BroadcastsToAllClients` (lines 107-108) and add `Source` to prompts
6. Add `Source` setup to remaining test mocks (lines 155-156, 180-181, 204-205)

**Implementation Details**:
- All mocks need `client.Setup(c => c.Source).Returns(MessageSource.WebUi);` to pass
- ChatPrompts in tests need `Source` property set
- Existing broadcast test needs both clients set to WebUi to maintain broadcast behavior

**Reference Implementation** (updated test class):
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
        client1.Setup(c => c.Source).Returns(MessageSource.WebUi);

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.SupportsScheduledNotifications).Returns(true);
        client2.Setup(c => c.Source).Returns(MessageSource.WebUi);

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
        client1.Setup(c => c.Source).Returns(MessageSource.WebUi);

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.SupportsScheduledNotifications).Returns(false);
        client2.Setup(c => c.Source).Returns(MessageSource.WebUi);

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
            Sender = "user1",
            Source = MessageSource.WebUi
        };

        var prompt2 = new ChatPrompt
        {
            Prompt = "From client 2",
            ChatId = 2,
            ThreadId = 2,
            MessageId = 2,
            Sender = "user2",
            Source = MessageSource.WebUi
        };

        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { prompt1 }.ToAsyncEnumerable());
        client1.Setup(c => c.Source).Returns(MessageSource.WebUi);

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { prompt2 }.ToAsyncEnumerable());
        client2.Setup(c => c.Source).Returns(MessageSource.WebUi);

        var composite = new CompositeChatMessengerClient([client1.Object, client2.Object]);

        // Act
        var prompts = new List<ChatPrompt>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await foreach (var prompt in composite.ReadPrompts(100, cts.Token))
            {
                prompts.Add(prompt);
                if (prompts.Count >= 2)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }

        // Assert
        prompts.Count.ShouldBe(2);
        prompts.ShouldContain(p => p.Prompt == "From client 1");
        prompts.ShouldContain(p => p.Prompt == "From client 2");
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_BroadcastsToWebUiClients()
    {
        // Arrange
        var receivedUpdates1 = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();
        var receivedUpdates2 = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();

        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.Source).Returns(MessageSource.WebUi);
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
        client2.Setup(c => c.Source).Returns(MessageSource.WebUi);
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

        // Assert - Both WebUi clients receive updates (universal viewers)
        receivedUpdates1.Count.ShouldBe(1);
        receivedUpdates2.Count.ShouldBe(1);
        receivedUpdates1[0].Item2.Contents.OfType<TextContent>().ShouldContain(tc => tc.Text == "Hello");
        receivedUpdates2[0].Item2.Contents.OfType<TextContent>().ShouldContain(tc => tc.Text == "Hello");
    }

    [Fact]
    public async Task DoesThreadExist_ReturnsTrueIfAnyClientReturnsTrue()
    {
        // Arrange
        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.Source).Returns(MessageSource.WebUi);
        client1.Setup(c => c.DoesThreadExist(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.Source).Returns(MessageSource.WebUi);
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
        client1.Setup(c => c.Source).Returns(MessageSource.WebUi);
        client1.Setup(c => c.CreateTopicIfNeededAsync(It.IsAny<long?>(), It.IsAny<long?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedKey);

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.Source).Returns(MessageSource.WebUi);

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
        client1.Setup(c => c.Source).Returns(MessageSource.WebUi);
        client1.Setup(c => c.StartScheduledStreamAsync(It.IsAny<AgentKey>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.Source).Returns(MessageSource.WebUi);
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

**Dependencies**: `Domain/DTOs/MessageSource.cs`
**Provides**: Updated test coverage for existing functionality

## Dependency Graph

> Files in the same phase can execute in parallel.

| Phase | File | Action | Depends On |
|-------|------|--------|------------|
| 1 | `Domain/DTOs/MessageSource.cs` | create | - |
| 2 | `Domain/Contracts/IChatMessengerClient.cs` | edit | `Domain/DTOs/MessageSource.cs` |
| 2 | `Domain/DTOs/ChatPrompt.cs` | edit | `Domain/DTOs/MessageSource.cs` |
| 3 | `Tests/Unit/Infrastructure/Messaging/MessageSourceRoutingTests.cs` | create | `Domain/DTOs/MessageSource.cs`, `Domain/Contracts/IChatMessengerClient.cs`, `Domain/DTOs/ChatPrompt.cs` |
| 3 | `Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs` | edit | `Domain/DTOs/MessageSource.cs`, `Domain/Contracts/IChatMessengerClient.cs` |
| 4 | `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs` | edit | `Tests/Unit/Infrastructure/Messaging/MessageSourceRoutingTests.cs` |
| 4 | `Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs` | edit | `Domain/DTOs/MessageSource.cs`, `Domain/Contracts/IChatMessengerClient.cs` |
| 4 | `Infrastructure/Clients/Messaging/TelegramChatClient.cs` | edit | `Domain/DTOs/MessageSource.cs`, `Domain/Contracts/IChatMessengerClient.cs` |
| 4 | `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs` | edit | `Domain/DTOs/MessageSource.cs`, `Domain/Contracts/IChatMessengerClient.cs` |
| 4 | `Infrastructure/Clients/Messaging/CliChatMessengerClient.cs` | edit | `Domain/DTOs/MessageSource.cs`, `Domain/Contracts/IChatMessengerClient.cs` |
| 4 | `Infrastructure/Clients/Messaging/OneShotChatMessengerClient.cs` | edit | `Domain/DTOs/MessageSource.cs`, `Domain/Contracts/IChatMessengerClient.cs` |

## Exit Criteria

### Test Commands
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessageSourceRoutingTests|FullyQualifiedName~CompositeChatMessengerClientTests"
dotnet build Agent/Agent.csproj
```

### Success Conditions
- [ ] All tests pass (exit code 0)
- [ ] Project builds without errors (exit code 0)
- [ ] All requirements satisfied:
  - [ ] WebUI receives ALL responses
  - [ ] ServiceBus receives ONLY ServiceBus-originated responses
  - [ ] Telegram receives ONLY Telegram-originated responses
  - [ ] CLI/OneShot receive ONLY CLI-originated responses
  - [ ] Unknown ChatIds broadcast to all (fail-safe)
- [ ] All files implemented

### Verification Script
```bash
dotnet build Agent/Agent.csproj && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessageSourceRoutingTests|FullyQualifiedName~CompositeChatMessengerClientTests"
```

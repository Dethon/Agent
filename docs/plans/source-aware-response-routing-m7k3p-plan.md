# Architectural Plan: Source-Aware Response Routing

## Summary

Refactor `CompositeChatMessengerClient` to pass the prompt's `MessageSource` through the response stream tuple, eliminating the flaky `_chatIdToSource` dictionary that fails when two prompts from different sources share the same `ChatId`. This enables deterministic, per-prompt source routing instead of relying on ChatId-to-source mappings that can be overwritten by subsequent prompts.

## Files

> **Note**: This is the canonical file list.

### Files to Edit
- `Domain/Contracts/IChatMessengerClient.cs`
- `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs`
- `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs`
- `Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs`
- `Infrastructure/Clients/Messaging/CliChatMessengerClient.cs`
- `Infrastructure/Clients/Messaging/OneShotChatMessengerClient.cs`
- `Infrastructure/Clients/Messaging/TelegramChatClient.cs`
- `Domain/Monitor/ChatMonitor.cs`
- `Domain/Monitor/ScheduleExecutor.cs`
- `Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs`
- `Tests/Unit/Infrastructure/Messaging/MessageSourceRoutingTests.cs`

### Files to Create
- None

## Code Context

### Current Implementation (CompositeChatMessengerClient.cs)

**Problem location**: Lines 15, 31, 108

```csharp
// Line 15: Dictionary mapping ChatId to Source - THIS IS THE PROBLEM
private readonly ConcurrentDictionary<long, MessageSource> _chatIdToSource = new();

// Line 31: Stores source when reading prompt (can be overwritten by later prompts with same ChatId)
_chatIdToSource[prompt.ChatId] = prompt.Source;

// Lines 108-114: Routing logic uses _chatIdToSource lookup
var isKnownChatId = _chatIdToSource.TryGetValue(agentKey.ChatId, out var promptSource);
var writeTasks = clientChannelPairs
    .Where(pair =>
        pair.client.Source == MessageSource.WebUi ||
        !isKnownChatId ||
        pair.client.Source == promptSource)
```

### IChatMessengerClient Interface (Domain/Contracts/IChatMessengerClient.cs)

Current signature at line 15-16:
```csharp
Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates, CancellationToken cancellationToken);
```

### ChatMonitor (Domain/Monitor/ChatMonitor.cs)

Lines 28-33 - creates response stream and calls ProcessResponseStreamAsync:
```csharp
var responses = chatMessengerClient.ReadPrompts(1000, cancellationToken)
    .GroupByStreaming(...)
    .Select(group => ProcessChatThread(group.Key, group, cancellationToken))
    .Merge(cancellationToken);
await chatMessengerClient.ProcessResponseStreamAsync(responses, cancellationToken);
```

Lines 46-92 - ProcessChatThread yields `(AgentKey, AgentResponseUpdate, AiResponse?)`:
```csharp
private async IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> ProcessChatThread(
    AgentKey agentKey,
    IAsyncGrouping<AgentKey, ChatPrompt> group,
    ...)
{
    // Line 51: firstPrompt has .Source property
    var firstPrompt = await group.FirstAsync(ct);
    // ... processing ...
    // Line 91: Yields without source info
    yield return (agentKey, update, aiResponse);
}
```

### ScheduleExecutor (Domain/Monitor/ScheduleExecutor.cs)

Lines 57-61 - calls CreateTopicIfNeededAsync, StartScheduledStreamAsync, ProcessResponseStreamAsync:
```csharp
await messengerClient.StartScheduledStreamAsync(agentKey, ct);
var responses = ExecuteScheduleCore(schedule, agentKey, schedule.UserId, ct);
await messengerClient.ProcessResponseStreamAsync(
    responses.Select(r => (agentKey, r.Update, r.AiResponse)), ct);
```

### MessageSource Enum (Domain/DTOs/MessageSource.cs)

```csharp
public enum MessageSource
{
    WebUi,
    ServiceBus,
    Telegram,
    Cli
}
```

### ChatPrompt DTO (Domain/DTOs/ChatPrompt.cs)

Line 14 - Source property exists:
```csharp
public MessageSource Source { get; init; } = MessageSource.WebUi;
```

### Individual Client Implementations

All clients have `Source` property returning their specific MessageSource:
- `WebChatMessengerClient.cs:31` - `MessageSource.WebUi`
- `ServiceBusChatMessengerClient.cs:26` - `MessageSource.ServiceBus`
- `CliChatMessengerClient.cs:19` - `MessageSource.Cli`
- `OneShotChatMessengerClient.cs:24` - `MessageSource.Cli`
- `TelegramChatClient.cs:32` - `MessageSource.Telegram`

## External Context

N/A - This is an internal refactoring using existing .NET async enumerable patterns.

## Architectural Narrative

### Task

Refactor the response routing mechanism in `CompositeChatMessengerClient` to eliminate the `_chatIdToSource` dictionary. Instead of tracking ChatId-to-source mappings (which can be overwritten when prompts from different sources share the same ChatId), pass the prompt's source through the response stream tuple. This enables per-prompt source routing where each response is routed to the client matching the source of the prompt that generated it.

### Architecture

The messaging architecture consists of:

1. **IChatMessengerClient** (Domain/Contracts/IChatMessengerClient.cs:7-27) - Interface defining the contract for reading prompts and processing responses
2. **CompositeChatMessengerClient** (Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs:12-128) - Aggregates multiple clients, merges prompt streams, broadcasts/routes responses
3. **ChatMonitor** (Domain/Monitor/ChatMonitor.cs:13-99) - Orchestrates prompt reading and response processing via the messenger client
4. **ScheduleExecutor** (Domain/Monitor/ScheduleExecutor.cs:14-114) - Executes scheduled prompts and processes responses
5. **Individual clients** - WebChat, ServiceBus, CLI, OneShot, Telegram implementations

Data flow:
```
Client.ReadPrompts() → ChatMonitor/ScheduleExecutor → Agent → Response Stream → ProcessResponseStreamAsync() → Route to matching client(s)
```

### Selected Context

| File | Relevance |
|------|-----------|
| `IChatMessengerClient.cs` | Interface signature must change to include `MessageSource` in tuple |
| `CompositeChatMessengerClient.cs` | Main routing logic, remove `_chatIdToSource`, add source-aware routing |
| `ChatMonitor.cs` | Must propagate `ChatPrompt.Source` through response stream |
| `ScheduleExecutor.cs` | Must pass appropriate source when calling methods (scheduled tasks use `WebUi`) |
| `All client implementations` | Must adapt to new signature |

### Relationships

```
ChatMonitor
    └── uses IChatMessengerClient.ReadPrompts() [returns ChatPrompt with Source]
    └── uses IChatMessengerClient.ProcessResponseStreamAsync() [must include Source]

ScheduleExecutor
    └── uses IChatMessengerClient.CreateTopicIfNeededAsync() [needs source parameter]
    └── uses IChatMessengerClient.StartScheduledStreamAsync() [needs source parameter]
    └── uses IChatMessengerClient.ProcessResponseStreamAsync() [must include Source]

CompositeChatMessengerClient
    └── implements IChatMessengerClient
    └── aggregates multiple IChatMessengerClient instances
    └── routes responses based on MessageSource
```

### External Context

N/A

### Implementation Notes

1. **Tuple expansion**: Change `(AgentKey, AgentResponseUpdate, AiResponse?)` to `(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)` throughout the response stream
2. **Source propagation**: `ChatMonitor.ProcessChatThread` must capture `firstPrompt.Source` and yield it with each response
3. **Routing logic**: `CompositeChatMessengerClient.BroadcastUpdatesAsync` uses the source from the tuple instead of dictionary lookup
4. **Scheduled tasks**: Use `MessageSource.WebUi` as scheduled tasks are created via WebChat
5. **Source-aware methods**: `CreateTopicIfNeededAsync` and `StartScheduledStreamAsync` need source parameter to route to correct client
6. **Remove dictionary**: Delete `_chatIdToSource` field and tracking in `ReadPrompts`

### Ambiguities

**Decision made**: Scheduled tasks will use `MessageSource.WebUi` because:
- Scheduled tasks are created through the WebChat UI
- WebChat is the universal viewer (receives all responses)
- This maintains backward compatibility with existing scheduled task behavior

### Requirements

1. `ProcessResponseStreamAsync` signature must include `MessageSource` in the tuple: `IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>`
2. `ChatMonitor.ProcessChatThread` must propagate `ChatPrompt.Source` through the response stream
3. `CompositeChatMessengerClient.CreateTopicIfNeededAsync` must accept `MessageSource` parameter and only call the client matching that source
4. `CompositeChatMessengerClient.StartScheduledStreamAsync` must accept `MessageSource` parameter and only call the client matching that source
5. Remove `_chatIdToSource` dictionary from `CompositeChatMessengerClient`
6. `ScheduleExecutor` must pass `MessageSource.WebUi` when calling `CreateTopicIfNeededAsync`, `StartScheduledStreamAsync`, and `ProcessResponseStreamAsync`
7. All `IChatMessengerClient` implementations must adapt their `ProcessResponseStreamAsync` signatures
8. Existing routing tests must pass with the new implementation
9. WebUi client must continue to receive all responses (universal viewer behavior)

### Constraints

- Must maintain backward compatibility with existing test patterns
- Must not change the `ChatPrompt` DTO structure
- Must follow existing LINQ-over-loops coding style
- Must use primary constructors for dependency injection

### Selected Approach

**Approach**: Source-in-Tuple with Source-Aware Method Routing

**Description**: Expand the `ProcessResponseStreamAsync` tuple to include `MessageSource`. Add `MessageSource` parameter to `CreateTopicIfNeededAsync` and `StartScheduledStreamAsync` in `IChatMessengerClient`. In `CompositeChatMessengerClient`:
- Remove the `_chatIdToSource` dictionary
- Route `ProcessResponseStreamAsync` updates based on the `MessageSource` in the tuple
- Route `CreateTopicIfNeededAsync` calls only to the client matching the source
- Route `StartScheduledStreamAsync` calls only to the client matching the source

**Rationale**:
- Eliminates race condition where later prompts overwrite source mappings
- Each response carries its own source, enabling deterministic routing
- Source-aware method routing ensures topic creation and stream starts only affect the relevant client
- Minimal interface changes - only tuple expansion and two method parameter additions

**Trade-offs Accepted**:
- All implementations must update their method signatures
- Slightly larger tuple in the response stream (4 elements instead of 3)

## Implementation Plan

### Domain/Contracts/IChatMessengerClient.cs [edit]

**Purpose**: Define the interface contract for chat messenger clients with source-aware routing

**TOTAL CHANGES**: 3

**Changes**:
1. Line 15-16: Expand `ProcessResponseStreamAsync` tuple to include `MessageSource`
2. Line 20-25: Add `MessageSource source` parameter to `CreateTopicIfNeededAsync`
3. Line 27: Add `MessageSource source` parameter to `StartScheduledStreamAsync`

**Implementation Details**:
- Tuple becomes `(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)`
- `CreateTopicIfNeededAsync` adds `MessageSource source` as first parameter
- `StartScheduledStreamAsync` adds `MessageSource source` as second parameter after `AgentKey`

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
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
        CancellationToken cancellationToken);

    Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId, CancellationToken cancellationToken);

    Task<AgentKey> CreateTopicIfNeededAsync(
        MessageSource source,
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default);

    Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default);
}
```

**Migration Pattern**:
```csharp
// BEFORE (line 15-16):
Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates, CancellationToken cancellationToken);

// AFTER:
Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
    CancellationToken cancellationToken);

// BEFORE (line 20-25):
Task<AgentKey> CreateTopicIfNeededAsync(
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default);

// AFTER:
Task<AgentKey> CreateTopicIfNeededAsync(
    MessageSource source,
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default);

// BEFORE (line 27):
Task StartScheduledStreamAsync(AgentKey agentKey, CancellationToken ct = default);

// AFTER:
Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default);
```

**Test File**: `Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs` - Tests updated in later phase

**Dependencies**: None
**Provides**: `IChatMessengerClient.ProcessResponseStreamAsync((AgentKey, AgentResponseUpdate, AiResponse?, MessageSource), CancellationToken)`, `IChatMessengerClient.CreateTopicIfNeededAsync(MessageSource, long?, long?, string?, string?, CancellationToken)`, `IChatMessengerClient.StartScheduledStreamAsync(AgentKey, MessageSource, CancellationToken)`

---

### Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs [edit]

**Purpose**: Test the CompositeChatMessengerClient routing behavior with source-aware tuple

**TOTAL CHANGES**: 6

**Changes**:
1. Lines 145-147: Update test tuple to include `MessageSource`
2. Lines 150-152: Update `ProcessResponseStreamAsync` call with 4-element tuple
3. Lines 190, 196, 204, 208: Update `CreateTopicIfNeededAsync` calls with `MessageSource.WebUi` parameter
4. Lines 216, 231: Update `StartScheduledStreamAsync` verification with source parameter
5. Lines 117-141: Update mock setup for new tuple signature
6. Update all test tuples to 4-element form

**Implementation Details**:
- All response tuples become `(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)`
- `CreateTopicIfNeededAsync` calls add `MessageSource.WebUi` as first argument
- `StartScheduledStreamAsync` verification adds source parameter

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
        client1.Setup(c => c.Source).Returns(MessageSource.WebUi);
        client1.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { prompt1 }.ToAsyncEnumerable());

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.Source).Returns(MessageSource.WebUi);
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
        var receivedUpdates1 = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();
        var receivedUpdates2 = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();

        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.Source).Returns(MessageSource.WebUi);
        client1.Setup(c => c.ProcessResponseStreamAsync(
                It.IsAny<IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
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
                It.IsAny<IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
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
            (AiResponse?)null,
            MessageSource.WebUi);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { testUpdate }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert
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
    public async Task CreateTopicIfNeededAsync_DelegatesToMatchingSourceClient()
    {
        // Arrange
        var expectedKey = new AgentKey(123, 456, "agent1");

        var webUiClient = new Mock<IChatMessengerClient>();
        webUiClient.Setup(c => c.Source).Returns(MessageSource.WebUi);
        webUiClient.Setup(c => c.CreateTopicIfNeededAsync(
                MessageSource.WebUi, It.IsAny<long?>(), It.IsAny<long?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedKey);

        var serviceBusClient = new Mock<IChatMessengerClient>();
        serviceBusClient.Setup(c => c.Source).Returns(MessageSource.ServiceBus);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, serviceBusClient.Object]);

        // Act
        var result = await composite.CreateTopicIfNeededAsync(MessageSource.WebUi, 123, 456, "agent1", "topic");

        // Assert
        result.ShouldBe(expectedKey);
        webUiClient.Verify(c => c.CreateTopicIfNeededAsync(MessageSource.WebUi, 123, 456, "agent1", "topic",
            It.IsAny<CancellationToken>()), Times.Once);
        serviceBusClient.Verify(c => c.CreateTopicIfNeededAsync(
            It.IsAny<MessageSource>(), It.IsAny<long?>(), It.IsAny<long?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartScheduledStreamAsync_DelegatesToMatchingSourceClient()
    {
        // Arrange
        var agentKey = new AgentKey(1, 1, "agent");

        var webUiClient = new Mock<IChatMessengerClient>();
        webUiClient.Setup(c => c.Source).Returns(MessageSource.WebUi);
        webUiClient.Setup(c => c.StartScheduledStreamAsync(It.IsAny<AgentKey>(), MessageSource.WebUi,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var serviceBusClient = new Mock<IChatMessengerClient>();
        serviceBusClient.Setup(c => c.Source).Returns(MessageSource.ServiceBus);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, serviceBusClient.Object]);

        // Act
        await composite.StartScheduledStreamAsync(agentKey, MessageSource.WebUi);

        // Assert
        webUiClient.Verify(c => c.StartScheduledStreamAsync(agentKey, MessageSource.WebUi,
            It.IsAny<CancellationToken>()), Times.Once);
        serviceBusClient.Verify(c => c.StartScheduledStreamAsync(
            It.IsAny<AgentKey>(), It.IsAny<MessageSource>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
```

**Dependencies**: `Domain/Contracts/IChatMessengerClient.cs`
**Provides**: Test coverage for `CompositeChatMessengerClient` with new tuple signature

---

### Tests/Unit/Infrastructure/Messaging/MessageSourceRoutingTests.cs [edit]

**Purpose**: Test source-aware routing behavior in CompositeChatMessengerClient

**TOTAL CHANGES**: 5

**Changes**:
1. Lines 18-19, 47-50, 67-68, 96-99, etc.: Update tuple type from 3 to 4 elements
2. Lines 47-50, 96-99, 127-130, 175-178, 236-239: Update response tuples to include source
3. Lines 251-268: Update CreateMockClient to use 4-element tuple
4. Remove ReadPrompts calls that establish source tracking (no longer needed)
5. Update all test assertions to work with direct source routing

**Implementation Details**:
- Tuples become `(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)`
- Source is passed directly in tuple, not looked up from dictionary
- Tests verify routing based on tuple's MessageSource

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
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();
        var serviceBusUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus, serviceBusUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, serviceBusClient.Object]);

        // Create response with ServiceBus source - both WebUI and ServiceBus should receive
        var response = (
            new AgentKey(123, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Response")] },
            (AiResponse?)null,
            MessageSource.ServiceBus);

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
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();
        var serviceBusUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus, serviceBusUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, serviceBusClient.Object]);

        // Create response with WebUI source - ServiceBus should NOT receive
        var response = (
            new AgentKey(456, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Response")] },
            (AiResponse?)null,
            MessageSource.WebUi);

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
    public async Task ProcessResponseStreamAsync_TelegramClientOnlyReceivesTelegramResponses()
    {
        // Arrange
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();
        var telegramUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var telegramClient = CreateMockClient(MessageSource.Telegram, telegramUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, telegramClient.Object]);

        // Create response with Telegram source
        var response = (
            new AgentKey(100, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Response")] },
            (AiResponse?)null,
            MessageSource.Telegram);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { response }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert
        webUiUpdates.Count.ShouldBe(1);  // WebUI receives all
        telegramUpdates.Count.ShouldBe(1);  // Telegram receives its own
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_SameChatIdDifferentSources_RoutesCorrectly()
    {
        // Arrange
        var webUiUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();
        var serviceBusUpdates = new List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>();

        var webUiClient = CreateMockClient(MessageSource.WebUi, webUiUpdates);
        var serviceBusClient = CreateMockClient(MessageSource.ServiceBus, serviceBusUpdates);

        var composite = new CompositeChatMessengerClient([webUiClient.Object, serviceBusClient.Object]);

        // Same ChatId but different sources - should route to correct client based on tuple source
        var webUiResponse = (
            new AgentKey(200, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("WebUI Response")] },
            (AiResponse?)null,
            MessageSource.WebUi);

        var serviceBusResponse = (
            new AgentKey(200, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("ServiceBus Response")] },
            (AiResponse?)null,
            MessageSource.ServiceBus);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { webUiResponse, serviceBusResponse }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert
        // WebUI receives both (universal viewer)
        webUiUpdates.Count.ShouldBe(2);
        // ServiceBus only receives its own
        serviceBusUpdates.Count.ShouldBe(1);
        serviceBusUpdates[0].Item2.Contents.OfType<TextContent>()
            .ShouldContain(tc => tc.Text == "ServiceBus Response");
    }

    private static Mock<IChatMessengerClient> CreateMockClient(
        MessageSource source,
        List<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> receivedUpdates)
    {
        var mock = new Mock<IChatMessengerClient>();
        mock.Setup(c => c.Source).Returns(source);
        mock.Setup(c => c.SupportsScheduledNotifications).Returns(false);
        mock.Setup(c => c.ProcessResponseStreamAsync(
                It.IsAny<IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
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

**Dependencies**: `Domain/Contracts/IChatMessengerClient.cs`
**Provides**: Test coverage for source-aware routing behavior

---

### Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs [edit]

**Purpose**: Aggregate multiple messenger clients with source-aware response routing

**TOTAL CHANGES**: 6

**Changes**:
1. Line 15: Remove `_chatIdToSource` dictionary field
2. Lines 21-34: Remove source tracking from `ReadPrompts` (delete lines 31)
3. Lines 36-59: Update `ProcessResponseStreamAsync` signature and channel types for 4-element tuple
4. Lines 71-81: Update `CreateTopicIfNeededAsync` to accept `MessageSource` and route to matching client
5. Lines 83-87: Update `StartScheduledStreamAsync` to accept `MessageSource` and route to matching client
6. Lines 97-127: Update `BroadcastUpdatesAsync` to use source from tuple instead of dictionary lookup

**Implementation Details**:
- Remove `_chatIdToSource` field entirely
- `ProcessResponseStreamAsync` takes `(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)` tuple
- `CreateTopicIfNeededAsync` routes to client where `client.Source == source` parameter
- `StartScheduledStreamAsync` routes to client where `client.Source == source` parameter
- `BroadcastUpdatesAsync` extracts source from tuple: `var (agentKey, _, _, source) = update;`
- Routing logic: `pair.client.Source == MessageSource.WebUi || pair.client.Source == source`

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
    public MessageSource Source => MessageSource.WebUi;

    public bool SupportsScheduledNotifications => clients.Any(c => c.SupportsScheduledNotifications);

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Validate();
        var merged = clients
            .Select(c => c.ReadPrompts(timeout, cancellationToken))
            .Merge(cancellationToken);

        await foreach (var prompt in merged)
        {
            yield return prompt;
        }
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
        var matchingClient = clients.FirstOrDefault(c => c.Source == source)
            ?? throw new InvalidOperationException($"No client found for source {source}");
        return await matchingClient.CreateTopicIfNeededAsync(source, chatId, threadId, agentId, topicName, ct);
    }

    public async Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
    {
        Validate();
        var matchingClient = clients.FirstOrDefault(c => c.Source == source);
        if (matchingClient is not null)
        {
            await matchingClient.StartScheduledStreamAsync(agentKey, source, ct);
        }
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

                var writeTasks = clientChannelPairs
                    .Where(pair =>
                        pair.client.Source == MessageSource.WebUi ||
                        pair.client.Source == messageSource)
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

**Migration Pattern**:
```csharp
// BEFORE (line 15):
private readonly ConcurrentDictionary<long, MessageSource> _chatIdToSource = new();

// AFTER:
// Removed entirely

// BEFORE (lines 29-33):
await foreach (var prompt in merged)
{
    _chatIdToSource[prompt.ChatId] = prompt.Source;
    yield return prompt;
}

// AFTER:
await foreach (var prompt in merged)
{
    yield return prompt;
}

// BEFORE (lines 36-38):
public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
    CancellationToken cancellationToken)

// AFTER:
public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
    CancellationToken cancellationToken)

// BEFORE (lines 71-81):
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

// AFTER:
public async Task<AgentKey> CreateTopicIfNeededAsync(
    MessageSource source,
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)
{
    Validate();
    var matchingClient = clients.FirstOrDefault(c => c.Source == source)
        ?? throw new InvalidOperationException($"No client found for source {source}");
    return await matchingClient.CreateTopicIfNeededAsync(source, chatId, threadId, agentId, topicName, ct);
}

// BEFORE (lines 83-87):
public async Task StartScheduledStreamAsync(AgentKey agentKey, CancellationToken ct = default)
{
    Validate();
    await Task.WhenAll(clients.Select(c => c.StartScheduledStreamAsync(agentKey, ct)));
}

// AFTER:
public async Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
{
    Validate();
    var matchingClient = clients.FirstOrDefault(c => c.Source == source);
    if (matchingClient is not null)
    {
        await matchingClient.StartScheduledStreamAsync(agentKey, source, ct);
    }
}

// BEFORE (lines 105-114):
var (agentKey, _, _) = update;
var isKnownChatId = _chatIdToSource.TryGetValue(agentKey.ChatId, out var promptSource);
var writeTasks = clientChannelPairs
    .Where(pair =>
        pair.client.Source == MessageSource.WebUi ||
        !isKnownChatId ||
        pair.client.Source == promptSource)
    .Select(pair => pair.channel.Writer.WriteAsync(update, ct).AsTask());

// AFTER:
var (_, _, _, messageSource) = update;
var writeTasks = clientChannelPairs
    .Where(pair =>
        pair.client.Source == MessageSource.WebUi ||
        pair.client.Source == messageSource)
    .Select(pair => pair.channel.Writer.WriteAsync(update, ct).AsTask());
```

**Dependencies**: `Domain/Contracts/IChatMessengerClient.cs`, `Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs`, `Tests/Unit/Infrastructure/Messaging/MessageSourceRoutingTests.cs`
**Provides**: `CompositeChatMessengerClient` with source-aware routing

---

### Domain/Monitor/ChatMonitor.cs [edit]

**Purpose**: Orchestrate prompt reading and response processing with source propagation

**TOTAL CHANGES**: 3

**Changes**:
1. Lines 25-26: Update `CreateTopicIfNeededAsync` call to include `x.Source` as first parameter
2. Line 46: Update `ProcessChatThread` return type to 4-element tuple
3. Line 91: Yield with `firstPrompt.Source` captured from the first prompt

**Implementation Details**:
- `CreateTopicIfNeededAsync` call adds `x.Source` as first argument
- `ProcessChatThread` signature returns `IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>`
- Capture `firstPrompt.Source` and yield it with each response: `yield return (agentKey, update, aiResponse, firstPrompt.Source);`

**Reference Implementation**:
```csharp
using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ChatMonitor(
    IChatMessengerClient chatMessengerClient,
    IAgentFactory agentFactory,
    ChatThreadResolver threadResolver,
    ILogger<ChatMonitor> logger)
{
    public async Task Monitor(CancellationToken cancellationToken)
    {
        try
        {
            var responses = chatMessengerClient.ReadPrompts(1000, cancellationToken)
                .GroupByStreaming(
                    async (x, ct) => await chatMessengerClient.CreateTopicIfNeededAsync(
                        x.Source, x.ChatId, x.ThreadId, x.AgentId, x.Prompt, ct),
                    cancellationToken)
                .Select(group => ProcessChatThread(group.Key, group, cancellationToken))
                .Merge(cancellationToken);

            try
            {
                await chatMessengerClient.ProcessResponseStreamAsync(responses, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Inner ChatMonitor exception: {exceptionMessage}", ex.Message);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ChatMonitor exception: {exceptionMessage}", ex.Message);
        }
    }

    private async IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> ProcessChatThread(
        AgentKey agentKey,
        IAsyncGrouping<AgentKey, ChatPrompt> group,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var firstPrompt = await group.FirstAsync(ct);
        var promptSource = firstPrompt.Source;
        await using var agent = agentFactory.Create(agentKey, firstPrompt.Sender, firstPrompt.AgentId);
        var context = threadResolver.Resolve(agentKey);
        var thread = await GetOrRestoreThread(agent, agentKey, ct);

        context.RegisterCompletionCallback(group.Complete);

        using var linkedCts = context.GetLinkedTokenSource(ct);
        var linkedCt = linkedCts.Token;

        // ReSharper disable once AccessToDisposedClosure - agent and threadCts are disposed after await foreach completes
        var aiResponses = group.Prepend(firstPrompt)
            .Select(async (x, _, _) =>
            {
                var command = ChatCommandParser.Parse(x.Prompt);
                switch (command)
                {
                    case ChatCommand.Clear:
                        await threadResolver.ClearAsync(agentKey);
                        return AsyncEnumerable.Empty<(AgentResponseUpdate, AiResponse?)>();
                    case ChatCommand.Cancel:
                        threadResolver.Cancel(agentKey);
                        return AsyncEnumerable.Empty<(AgentResponseUpdate, AiResponse?)>();
                    default:
                        var userMessage = new ChatMessage(ChatRole.User, x.Prompt);
                        userMessage.SetSenderId(x.Sender);
                        userMessage.SetTimestamp(DateTimeOffset.UtcNow);
                        return agent
                            .RunStreamingAsync([userMessage], thread, cancellationToken: linkedCt)
                            .WithErrorHandling(linkedCt)
                            .ToUpdateAiResponsePairs()
                            .Append((
                                new AgentResponseUpdate { Contents = [new StreamCompleteContent()] },
                                null));
                }
            })
            .Merge(linkedCt);

        await foreach (var (update, aiResponse) in aiResponses.WithCancellation(ct))
        {
            yield return (agentKey, update, aiResponse, promptSource);
        }
    }

    private static ValueTask<AgentSession> GetOrRestoreThread(
        DisposableAgent agent, AgentKey agentKey, CancellationToken ct)
    {
        return agent.DeserializeSessionAsync(JsonSerializer.SerializeToElement(agentKey.ToString()), null, ct);
    }
}
```

**Migration Pattern**:
```csharp
// BEFORE (lines 25-26):
async (x, ct) => await chatMessengerClient.CreateTopicIfNeededAsync(
    x.ChatId, x.ThreadId, x.AgentId, x.Prompt, ct),

// AFTER:
async (x, ct) => await chatMessengerClient.CreateTopicIfNeededAsync(
    x.Source, x.ChatId, x.ThreadId, x.AgentId, x.Prompt, ct),

// BEFORE (line 46):
private async IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> ProcessChatThread(

// AFTER:
private async IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> ProcessChatThread(

// BEFORE (around line 51, add after firstPrompt):
var firstPrompt = await group.FirstAsync(ct);
await using var agent = agentFactory.Create(agentKey, firstPrompt.Sender, firstPrompt.AgentId);

// AFTER:
var firstPrompt = await group.FirstAsync(ct);
var promptSource = firstPrompt.Source;
await using var agent = agentFactory.Create(agentKey, firstPrompt.Sender, firstPrompt.AgentId);

// BEFORE (line 91):
yield return (agentKey, update, aiResponse);

// AFTER:
yield return (agentKey, update, aiResponse, promptSource);
```

**Dependencies**: `Domain/Contracts/IChatMessengerClient.cs`
**Provides**: Source-propagating response stream for `ProcessResponseStreamAsync`

---

### Domain/Monitor/ScheduleExecutor.cs [edit]

**Purpose**: Execute scheduled prompts with correct source for routing

**TOTAL CHANGES**: 3

**Changes**:
1. Lines 37-42: Update `CreateTopicIfNeededAsync` call to include `MessageSource.WebUi` as first parameter
2. Line 57: Update `StartScheduledStreamAsync` call to include `MessageSource.WebUi` as second parameter
3. Lines 60-61: Update `ProcessResponseStreamAsync` call to include `MessageSource.WebUi` in tuple

**Implementation Details**:
- Scheduled tasks use `MessageSource.WebUi` because they are created via WebChat
- `CreateTopicIfNeededAsync` call adds `MessageSource.WebUi` as first argument
- `StartScheduledStreamAsync` call adds `MessageSource.WebUi` as second argument
- Response tuple adds `MessageSource.WebUi` as fourth element

**Reference Implementation**:
```csharp
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ScheduleExecutor(
    IScheduleStore store,
    IScheduleAgentFactory agentFactory,
    IChatMessengerClient messengerClient,
    Channel<Schedule> scheduleChannel,
    ILogger<ScheduleExecutor> logger)
{
    public async Task ProcessSchedulesAsync(CancellationToken ct)
    {
        await foreach (var schedule in scheduleChannel.Reader.ReadAllAsync(ct))
        {
            await ProcessScheduleAsync(schedule, ct);
        }
    }

    private async Task ProcessScheduleAsync(Schedule schedule, CancellationToken ct)
    {
        AgentKey agentKey;

        if (messengerClient.SupportsScheduledNotifications)
        {
            try
            {
                agentKey = await messengerClient.CreateTopicIfNeededAsync(
                    MessageSource.WebUi,
                    chatId: null,
                    threadId: null,
                    agentId: schedule.Agent.Id,
                    topicName: "Scheduled task",
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error creating topic for schedule {ScheduleId}", schedule.Id);
                return;
            }

            logger.LogInformation(
                "Executing schedule {ScheduleId} for agent {AgentName} on thread {ThreadId}",
                schedule.Id,
                schedule.Agent.Name,
                agentKey.ThreadId);


            await messengerClient.StartScheduledStreamAsync(agentKey, MessageSource.WebUi, ct);

            var responses = ExecuteScheduleCore(schedule, agentKey, schedule.UserId, ct);
            await messengerClient.ProcessResponseStreamAsync(
                responses.Select(r => (agentKey, r.Update, r.AiResponse, MessageSource.WebUi)), ct);
        }
        else
        {
            logger.LogInformation(
                "Executing schedule {ScheduleId} for agent {AgentName} silently (no notification support)",
                schedule.Id,
                schedule.Agent.Name);

            agentKey = new AgentKey(0, 0, schedule.Agent.Id);

            await foreach (var _ in ExecuteScheduleCore(schedule, agentKey, schedule.UserId, ct))
            {
                // Consume the stream silently
            }
        }

        if (schedule.CronExpression is null)
        {
            await store.DeleteAsync(schedule.Id, ct);
        }
    }

    private async IAsyncEnumerable<(AgentResponseUpdate Update, AiResponse? AiResponse)> ExecuteScheduleCore(
        Schedule schedule,
        AgentKey agentKey,
        string? userId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var agent = agentFactory.CreateFromDefinition(
            agentKey,
            schedule.UserId ?? "scheduler",
            schedule.Agent);

        var thread = await agent.DeserializeSessionAsync(
            JsonSerializer.SerializeToElement(agentKey.ToString()),
            null,
            ct);

        var userMessage = new ChatMessage(ChatRole.User, schedule.Prompt);
        userMessage.SetSenderId(userId);
        userMessage.SetTimestamp(DateTimeOffset.UtcNow);

        await foreach (var (update, aiResponse) in agent
                           .RunStreamingAsync([userMessage], thread, cancellationToken: ct)
                           .WithErrorHandling(ct)
                           .ToUpdateAiResponsePairs()
                           .WithCancellation(ct))
        {
            yield return (update, aiResponse);
        }

        yield return (new AgentResponseUpdate { Contents = [new StreamCompleteContent()] }, null);
    }
}
```

**Migration Pattern**:
```csharp
// BEFORE (lines 37-42):
agentKey = await messengerClient.CreateTopicIfNeededAsync(
    chatId: null,
    threadId: null,
    agentId: schedule.Agent.Id,
    topicName: "Scheduled task",
    ct);

// AFTER:
agentKey = await messengerClient.CreateTopicIfNeededAsync(
    MessageSource.WebUi,
    chatId: null,
    threadId: null,
    agentId: schedule.Agent.Id,
    topicName: "Scheduled task",
    ct);

// BEFORE (line 57):
await messengerClient.StartScheduledStreamAsync(agentKey, ct);

// AFTER:
await messengerClient.StartScheduledStreamAsync(agentKey, MessageSource.WebUi, ct);

// BEFORE (lines 60-61):
await messengerClient.ProcessResponseStreamAsync(
    responses.Select(r => (agentKey, r.Update, r.AiResponse)), ct);

// AFTER:
await messengerClient.ProcessResponseStreamAsync(
    responses.Select(r => (agentKey, r.Update, r.AiResponse, MessageSource.WebUi)), ct);
```

**Dependencies**: `Domain/Contracts/IChatMessengerClient.cs`
**Provides**: Scheduled task execution with correct source routing

---

### Infrastructure/Clients/Messaging/WebChatMessengerClient.cs [edit]

**Purpose**: WebChat client implementation with updated interface signature

**TOTAL CHANGES**: 3

**Changes**:
1. Lines 43-45: Update `ProcessResponseStreamAsync` signature for 4-element tuple
2. Line 47: Update tuple deconstruction to include MessageSource (ignored)
3. Lines 157-162, 95-103: Update `CreateTopicIfNeededAsync` and `StartScheduledStreamAsync` signatures

**Implementation Details**:
- `ProcessResponseStreamAsync` takes `(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)` tuple
- Deconstruction ignores the MessageSource: `var (key, update, _, _) = ...`
- `CreateTopicIfNeededAsync` adds `MessageSource source` as first parameter (ignored for WebChat)
- `StartScheduledStreamAsync` adds `MessageSource source` as second parameter (ignored for WebChat)

**Reference Implementation**:
```csharp
// Only showing changed methods - rest of file unchanged

public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
    CancellationToken cancellationToken)
{
    await foreach (var (key, update, _, _) in updates.WithCancellation(cancellationToken))
    {
        // ... rest unchanged
    }
}

public async Task<AgentKey> CreateTopicIfNeededAsync(
    MessageSource source,
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)
{
    // ... rest unchanged
}

public async Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
{
    // ... rest unchanged
}
```

**Migration Pattern**:
```csharp
// BEFORE (lines 43-45):
public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
    CancellationToken cancellationToken)

// AFTER:
public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
    CancellationToken cancellationToken)

// BEFORE (line 47):
await foreach (var (key, update, _) in updates.WithCancellation(cancellationToken))

// AFTER:
await foreach (var (key, update, _, _) in updates.WithCancellation(cancellationToken))

// BEFORE (lines 157-162):
public async Task<AgentKey> CreateTopicIfNeededAsync(
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)

// AFTER:
public async Task<AgentKey> CreateTopicIfNeededAsync(
    MessageSource source,
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)

// BEFORE (line 190):
public async Task StartScheduledStreamAsync(AgentKey agentKey, CancellationToken ct = default)

// AFTER:
public async Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
```

**Dependencies**: `Domain/Contracts/IChatMessengerClient.cs`
**Provides**: WebChat client with updated interface compliance

---

### Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs [edit]

**Purpose**: ServiceBus client implementation with updated interface signature

**TOTAL CHANGES**: 3

**Changes**:
1. Lines 38-40: Update `ProcessResponseStreamAsync` signature for 4-element tuple
2. Line 42: Update tuple deconstruction to include MessageSource (ignored)
3. Lines 88-96, 98-101: Update `CreateTopicIfNeededAsync` and `StartScheduledStreamAsync` signatures

**Implementation Details**:
- `ProcessResponseStreamAsync` takes `(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)` tuple
- Deconstruction ignores the MessageSource: `var (key, update, _, _) = ...`
- `CreateTopicIfNeededAsync` adds `MessageSource source` as first parameter (ignored)
- `StartScheduledStreamAsync` adds `MessageSource source` as second parameter (ignored)

**Reference Implementation**:
```csharp
// Only showing changed methods - rest of file unchanged

public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
    CancellationToken cancellationToken)
{
    await foreach (var (key, update, _, _) in updates.WithCancellation(cancellationToken))
    {
        // ... rest unchanged
    }
}

public Task<AgentKey> CreateTopicIfNeededAsync(
    MessageSource source,
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)
{
    return Task.FromResult(new AgentKey(chatId ?? 0, threadId ?? 0, agentId ?? defaultAgentId));
}

public Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
{
    return Task.CompletedTask;
}
```

**Migration Pattern**:
```csharp
// BEFORE (lines 38-40):
public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
    CancellationToken cancellationToken)

// AFTER:
public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
    CancellationToken cancellationToken)

// BEFORE (line 42):
await foreach (var (key, update, _) in updates.WithCancellation(cancellationToken))

// AFTER:
await foreach (var (key, update, _, _) in updates.WithCancellation(cancellationToken))

// BEFORE (lines 88-96):
public Task<AgentKey> CreateTopicIfNeededAsync(
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)

// AFTER:
public Task<AgentKey> CreateTopicIfNeededAsync(
    MessageSource source,
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)

// BEFORE (lines 98-101):
public Task StartScheduledStreamAsync(AgentKey agentKey, CancellationToken ct = default)

// AFTER:
public Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
```

**Dependencies**: `Domain/Contracts/IChatMessengerClient.cs`
**Provides**: ServiceBus client with updated interface compliance

---

### Infrastructure/Clients/Messaging/CliChatMessengerClient.cs [edit]

**Purpose**: CLI client implementation with updated interface signature

**TOTAL CHANGES**: 3

**Changes**:
1. Lines 46-48: Update `ProcessResponseStreamAsync` signature for 4-element tuple
2. Line 53: Update tuple deconstruction to include MessageSource (ignored)
3. Lines 104-112, 114-117: Update `CreateTopicIfNeededAsync` and `StartScheduledStreamAsync` signatures

**Implementation Details**:
- `ProcessResponseStreamAsync` takes `(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)` tuple
- Deconstruction ignores the MessageSource: `var (_, update, _, _) = ...`
- `CreateTopicIfNeededAsync` adds `MessageSource source` as first parameter (ignored)
- `StartScheduledStreamAsync` adds `MessageSource source` as second parameter (ignored)

**Reference Implementation**:
```csharp
// Only showing changed methods - rest of file unchanged

public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
    CancellationToken cancellationToken)
{
    string? currentMessageId = null;
    var messageIndex = 0;

    await foreach (var (_, update, _, _) in updates.WithCancellation(cancellationToken))
    {
        // ... rest unchanged
    }
}

public Task<AgentKey> CreateTopicIfNeededAsync(
    MessageSource source,
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)
{
    return Task.FromResult(new AgentKey(chatId ?? 0, threadId ?? 0, agentId));
}

public Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
{
    return Task.CompletedTask;
}
```

**Migration Pattern**:
```csharp
// BEFORE (lines 46-48):
public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
    CancellationToken cancellationToken)

// AFTER:
public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
    CancellationToken cancellationToken)

// BEFORE (line 53):
await foreach (var (_, update, _) in updates.WithCancellation(cancellationToken))

// AFTER:
await foreach (var (_, update, _, _) in updates.WithCancellation(cancellationToken))

// BEFORE (lines 104-112):
public Task<AgentKey> CreateTopicIfNeededAsync(
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)

// AFTER:
public Task<AgentKey> CreateTopicIfNeededAsync(
    MessageSource source,
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)

// BEFORE (lines 114-117):
public Task StartScheduledStreamAsync(AgentKey agentKey, CancellationToken ct = default)

// AFTER:
public Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
```

**Dependencies**: `Domain/Contracts/IChatMessengerClient.cs`
**Provides**: CLI client with updated interface compliance

---

### Infrastructure/Clients/Messaging/OneShotChatMessengerClient.cs [edit]

**Purpose**: OneShot client implementation with updated interface signature

**TOTAL CHANGES**: 3

**Changes**:
1. Lines 47-49: Update `ProcessResponseStreamAsync` signature for 4-element tuple
2. Lines 51-53: Update LINQ query to handle 4-element tuple
3. Lines 95-103, 105-108: Update `CreateTopicIfNeededAsync` and `StartScheduledStreamAsync` signatures

**Implementation Details**:
- `ProcessResponseStreamAsync` takes `(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)` tuple
- LINQ query selects `x.Item3` (AiResponse) from 4-element tuple
- `CreateTopicIfNeededAsync` adds `MessageSource source` as first parameter (ignored)
- `StartScheduledStreamAsync` adds `MessageSource source` as second parameter (ignored)

**Reference Implementation**:
```csharp
// Only showing changed methods - rest of file unchanged

public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
    CancellationToken cancellationToken)
{
    var responses = updates
        .Where(x => x.Item3 is not null)
        .Select(x => x.Item3!);

    // ... rest unchanged
}

public Task<AgentKey> CreateTopicIfNeededAsync(
    MessageSource source,
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)
{
    return Task.FromResult(new AgentKey(chatId ?? 0, threadId ?? 0, agentId));
}

public Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
{
    return Task.CompletedTask;
}
```

**Migration Pattern**:
```csharp
// BEFORE (lines 47-49):
public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
    CancellationToken cancellationToken)

// AFTER:
public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
    CancellationToken cancellationToken)

// BEFORE (lines 95-103):
public Task<AgentKey> CreateTopicIfNeededAsync(
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)

// AFTER:
public Task<AgentKey> CreateTopicIfNeededAsync(
    MessageSource source,
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)

// BEFORE (lines 105-108):
public Task StartScheduledStreamAsync(AgentKey agentKey, CancellationToken ct = default)

// AFTER:
public Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
```

**Dependencies**: `Domain/Contracts/IChatMessengerClient.cs`
**Provides**: OneShot client with updated interface compliance

---

### Infrastructure/Clients/Messaging/TelegramChatClient.cs [edit]

**Purpose**: Telegram client implementation with updated interface signature

**TOTAL CHANGES**: 3

**Changes**:
1. Lines 67-69: Update `ProcessResponseStreamAsync` signature for 4-element tuple
2. Lines 71-73: Update LINQ query to handle 4-element tuple
3. Lines 141-146, 166-169: Update `CreateTopicIfNeededAsync` and `StartScheduledStreamAsync` signatures

**Implementation Details**:
- `ProcessResponseStreamAsync` takes `(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)` tuple
- LINQ query selects `(x.Item1, x.Item3!)` from 4-element tuple
- `CreateTopicIfNeededAsync` adds `MessageSource source` as first parameter (ignored)
- `StartScheduledStreamAsync` adds `MessageSource source` as second parameter (ignored)

**Reference Implementation**:
```csharp
// Only showing changed methods - rest of file unchanged

public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
    CancellationToken cancellationToken)
{
    var responses = updates
        .Where(x => x.Item3 is not null)
        .Select(x => (x.Item1, x.Item3!));

    // ... rest unchanged
}

public async Task<AgentKey> CreateTopicIfNeededAsync(
    MessageSource source,
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)
{
    // ... rest unchanged
}

public Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
{
    return Task.CompletedTask;
}
```

**Migration Pattern**:
```csharp
// BEFORE (lines 67-69):
public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
    CancellationToken cancellationToken)

// AFTER:
public async Task ProcessResponseStreamAsync(
    IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
    CancellationToken cancellationToken)

// BEFORE (lines 141-146):
public async Task<AgentKey> CreateTopicIfNeededAsync(
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)

// AFTER:
public async Task<AgentKey> CreateTopicIfNeededAsync(
    MessageSource source,
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)

// BEFORE (lines 166-169):
public Task StartScheduledStreamAsync(AgentKey agentKey, CancellationToken ct = default)

// AFTER:
public Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
```

**Dependencies**: `Domain/Contracts/IChatMessengerClient.cs`
**Provides**: Telegram client with updated interface compliance

---

## Dependency Graph

> Files in the same phase can execute in parallel.

| Phase | File | Action | Depends On |
|-------|------|--------|------------|
| 1 | `Domain/Contracts/IChatMessengerClient.cs` | edit | - |
| 2 | `Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs` | edit | `Domain/Contracts/IChatMessengerClient.cs` |
| 2 | `Tests/Unit/Infrastructure/Messaging/MessageSourceRoutingTests.cs` | edit | `Domain/Contracts/IChatMessengerClient.cs` |
| 3 | `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs` | edit | `Domain/Contracts/IChatMessengerClient.cs`, `Tests/Unit/Infrastructure/Messaging/CompositeChatMessengerClientTests.cs`, `Tests/Unit/Infrastructure/Messaging/MessageSourceRoutingTests.cs` |
| 3 | `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs` | edit | `Domain/Contracts/IChatMessengerClient.cs` |
| 3 | `Infrastructure/Clients/Messaging/ServiceBusChatMessengerClient.cs` | edit | `Domain/Contracts/IChatMessengerClient.cs` |
| 3 | `Infrastructure/Clients/Messaging/CliChatMessengerClient.cs` | edit | `Domain/Contracts/IChatMessengerClient.cs` |
| 3 | `Infrastructure/Clients/Messaging/OneShotChatMessengerClient.cs` | edit | `Domain/Contracts/IChatMessengerClient.cs` |
| 3 | `Infrastructure/Clients/Messaging/TelegramChatClient.cs` | edit | `Domain/Contracts/IChatMessengerClient.cs` |
| 4 | `Domain/Monitor/ChatMonitor.cs` | edit | `Domain/Contracts/IChatMessengerClient.cs` |
| 4 | `Domain/Monitor/ScheduleExecutor.cs` | edit | `Domain/Contracts/IChatMessengerClient.cs` |

## Exit Criteria

### Test Commands
```bash
dotnet test Tests/Unit --filter "FullyQualifiedName~CompositeChatMessengerClientTests|FullyQualifiedName~MessageSourceRoutingTests"
dotnet build
```

### Success Conditions
- [ ] All tests pass (exit code 0)
- [ ] No build errors (exit code 0)
- [ ] `_chatIdToSource` dictionary removed from `CompositeChatMessengerClient`
- [ ] `ProcessResponseStreamAsync` signature includes `MessageSource` in tuple
- [ ] `CreateTopicIfNeededAsync` routes to matching source client only
- [ ] `StartScheduledStreamAsync` routes to matching source client only
- [ ] `ChatMonitor` propagates `ChatPrompt.Source` through response stream
- [ ] `ScheduleExecutor` uses `MessageSource.WebUi` for scheduled tasks
- [ ] All `IChatMessengerClient` implementations updated
- [ ] WebUI client continues to receive all responses (universal viewer)

### Verification Script
```bash
dotnet build && dotnet test Tests/Unit --filter "FullyQualifiedName~CompositeChatMessengerClientTests|FullyQualifiedName~MessageSourceRoutingTests"
```

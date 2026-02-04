# Bug Scout Report: ServiceBus-to-WebUI Streaming Not Working

**Severity**: High
**Root Cause Confidence**: High
**Created**: 2026-02-04

## Summary

ServiceBus messages do not stream to WebUI in real-time because `CreateTopicIfNeededAsync` creates a session but not a stream for non-WebUI sources. The fix adds stream creation and WebUI client notification when the message source is not WebUI.

## Files

> **Note**: This is the canonical file list.

### Files to Edit
- `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs`

### Files to Create
- `Tests/Unit/Infrastructure/Messaging/WebChatMessengerClientTests.cs`

## Error Analysis

### Original Error

```
WARNING: "WriteMessage: topicId {TopicId} not found in _responseChannels"
```

This warning appears in `WebChatStreamManager.WriteMessageAsync` (line 81) when attempting to write a response to a stream that does not exist.

### Root Cause

The `WebChatMessengerClient.CreateTopicIfNeededAsync` method (lines 157-187) creates a session via `sessionManager.StartSession()` but does NOT create a stream via `streamManager.GetOrCreateStream()` for non-WebUI sources. When responses arrive and `ProcessResponseStreamAsync` calls `WriteMessageAsync`, the stream lookup fails because no stream entry exists.

### Code Path

```
ServiceBus Message
    -> ChatMonitor.EnqueueReceivedMessageAsync
    -> ChatPrompt with Source=ServiceBus
    -> CreateTopicIfNeededAsync
    -> sessionManager.StartSession() [session created]
    -> [NO stream created]
    -> Response arrives
    -> ProcessResponseStreamAsync
    -> sessionManager.GetTopicIdByChatId() [OK - finds topicId]
    -> streamManager.WriteMessageAsync()
    -> _responseChannels.TryGetValue() [FAILS - no stream entry]
    -> WARNING logged, message dropped
```

## Investigation Findings

### Evidence Collected

1. **WebChatMessengerClient.cs:157-187** - `CreateTopicIfNeededAsync` method creates session but not stream
2. **WebChatMessengerClient.cs:232-293** - `EnqueuePromptAndGetResponses` creates stream via `GetOrCreateStream` (WebUI flow only)
3. **WebChatMessengerClient.cs:191-206** - `StartScheduledStreamAsync` correctly creates stream for scheduled tasks (reference pattern)
4. **WebChatStreamManager.cs:79-83** - `WriteMessageAsync` logs warning when stream not found
5. **Domain/DTOs/MessageSource.cs:6-12** - MessageSource enum includes WebUi, ServiceBus, Telegram, Cli

### Hypothesis Testing

**Hypothesis**: Streams are only created in WebUI-originated flows, not external sources.

**Evidence For**:
- `EnqueuePromptAndGetResponses` (line 232) calls `GetOrCreateStream` - this is WebUI-only
- `StartScheduledStreamAsync` (line 191) calls `GetOrCreateStream` - for scheduled tasks
- `CreateTopicIfNeededAsync` does NOT call `GetOrCreateStream` - missing for ServiceBus/Telegram/CLI

**Evidence Against**: None found.

**Verdict**: CONFIRMED

### Root Cause Location

**File**: `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs`
**Method**: `CreateTopicIfNeededAsync`
**Lines**: 157-187
**Problem**: Missing stream creation for non-WebUI sources after line 184 (or line 179 for existing topics)

## Code Context

### WebChatMessengerClient.cs Analysis

The current implementation at lines 157-187:
```csharp
public async Task<AgentKey> CreateTopicIfNeededAsync(
    MessageSource source,
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)
{
    if (string.IsNullOrEmpty(agentId))
    {
        throw new ArgumentException("agentId is required for WebChat", nameof(agentId));
    }

    if (threadId.HasValue && chatId.HasValue)
    {
        var existingTopic = await threadStateStore.GetTopicByChatIdAndThreadIdAsync(
            agentId, chatId.Value, threadId.Value, ct);

        if (existingTopic is not null)
        {
            sessionManager.StartSession(existingTopic.TopicId, existingTopic.AgentId,
                existingTopic.ChatId, existingTopic.ThreadId);
            return new AgentKey(existingTopic.ChatId, existingTopic.ThreadId, existingTopic.AgentId);
        }
    }

    var actualChatId = chatId ?? GenerateChatId();
    var actualThreadId = await CreateThread(actualChatId, topicName ?? "Scheduled task", agentId, ct);

    return new AgentKey(actualChatId, actualThreadId, agentId);
}
```

### Reference Pattern: StartScheduledStreamAsync

The correct pattern exists at lines 191-206:
```csharp
public async Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
{
    var topicId = sessionManager.GetTopicIdByChatId(agentKey.ChatId);
    if (topicId is null)
    {
        logger.LogWarning("StartScheduledStreamAsync: topicId not found for chatId={ChatId}", agentKey.ChatId);
        return;
    }

    streamManager.GetOrCreateStream(topicId, "Scheduled task", null, ct);
    streamManager.TryIncrementPending(topicId);

    await hubNotifier.NotifyStreamChangedAsync(
            new StreamChangedNotification(StreamChangeType.Started, topicId), ct)
        .SafeAwaitAsync(logger, "Failed to notify stream started for topic {TopicId}", topicId);
}
```

### Key Dependencies

- `WebChatSessionManager.GetTopicIdByChatId(chatId)` - Returns topicId for a chatId
- `WebChatStreamManager.GetOrCreateStream(topicId, prompt, senderId, ct)` - Creates or returns existing stream
- `WebChatStreamManager.TryIncrementPending(topicId)` - Tracks pending prompts
- `INotifier.NotifyStreamChangedAsync(notification, ct)` - Notifies WebUI clients
- `SafeAwaitAsync(logger, message, args)` - Extension method for fire-and-forget with logging

## External Context

N/A - No external documentation needed. All required patterns exist within the codebase.

## Architectural Narrative

### Task

Fix the ServiceBus-to-WebUI streaming by ensuring `CreateTopicIfNeededAsync` creates a stream entry when the source is NOT WebUI. This allows `ProcessResponseStreamAsync` to successfully write messages to the stream.

### Architecture

The WebChat messaging system has three key managers:
1. **WebChatSessionManager** - Maps chatId to topicId, tracks sessions
2. **WebChatStreamManager** - Manages broadcast channels for streaming messages to WebUI
3. **WebChatApprovalManager** - Handles tool approval workflows

Message flow:
```
[Source] -> ChatMonitor -> CreateTopicIfNeededAsync -> [session + stream] -> ProcessResponseStreamAsync -> WriteMessageAsync -> [WebUI]
```

### Selected Context

- `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs` - Main fix location
- `Infrastructure/Clients/Messaging/WebChatStreamManager.cs` - Stream management (no changes needed)
- `Domain/DTOs/MessageSource.cs` - Source enum for condition check
- `Domain/DTOs/WebChat/HubNotification.cs` - StreamChangedNotification type

### Relationships

```
WebChatMessengerClient
    |
    +-- WebChatSessionManager (session tracking)
    +-- WebChatStreamManager (stream management)
    +-- INotifier (hubNotifier for WebUI notifications)
    +-- IThreadStateStore (topic persistence)
```

### External Context

N/A

### Implementation Notes

1. The fix follows the existing pattern from `StartScheduledStreamAsync` (lines 191-206)
2. Use `source != MessageSource.WebUi` condition - WebUI creates its own stream in `EnqueuePromptAndGetResponses`
3. Track topicId through both code paths (existing topic and new topic)
4. Use `SafeAwaitAsync` for notification to prevent failures from blocking the flow
5. Default topicName for stream should be consistent with existing code

### Ambiguities

**Decision Made**: Use `topicName ?? ""` as the prompt text for `GetOrCreateStream` since ServiceBus messages don't have a user prompt at this stage - the prompt is handled separately.

### Requirements

1. ServiceBus messages MUST stream to WebUI in real-time
2. Each ServiceBus sourceId MUST map to its own WebUI topic
3. WebUI clients MUST be notified when a ServiceBus conversation starts (StreamChangeType.Started)
4. Existing WebUI-originated message flow MUST NOT be affected (no stream created for WebUI source)

### Constraints

1. No changes to `WebChatStreamManager` - use existing APIs
2. No changes to `WebChatSessionManager` - use existing APIs
3. No changes to interface contracts
4. Follow existing patterns in codebase (SafeAwaitAsync, logging)

### Fix Strategy

**Approach**: Direct fix at source with conditional stream creation

**Description**: Modify `CreateTopicIfNeededAsync` to track the topicId through both code paths (existing topic and new topic), then conditionally create a stream and notify WebUI clients when the source is not WebUI.

**Rationale**: This is the minimal fix that addresses the root cause directly. The pattern already exists in `StartScheduledStreamAsync` which proves it works correctly. Adding the same logic to `CreateTopicIfNeededAsync` ensures all non-WebUI sources get proper stream handling.

**Trade-offs Accepted**: None - this fix is clean and follows existing patterns.

## Implementation Plan

### Tests/Unit/Infrastructure/Messaging/WebChatMessengerClientTests.cs [create]

**Purpose**: Unit tests for `CreateTopicIfNeededAsync` stream creation behavior

**TOTAL CHANGES**: 1 (create new file)

**Changes**:
1. Create new test file with tests for stream creation behavior

**Implementation Details**:
- Test class with constructor-injected mocks for all dependencies
- Implements `IDisposable` for cleanup
- Uses real `WebChatSessionManager` and `WebChatStreamManager` instances (no mocking)
- Mocks `IThreadStateStore`, `INotifier`

**Reference Implementation** (FULL code):
```csharp
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Messaging;

public sealed class WebChatMessengerClientTests : IDisposable
{
    private readonly Mock<IThreadStateStore> _threadStateStore;
    private readonly Mock<INotifier> _hubNotifier;
    private readonly WebChatSessionManager _sessionManager;
    private readonly WebChatStreamManager _streamManager;
    private readonly WebChatApprovalManager _approvalManager;
    private readonly ChatThreadResolver _threadResolver;
    private readonly WebChatMessengerClient _client;

    public WebChatMessengerClientTests()
    {
        _sessionManager = new WebChatSessionManager();
        _streamManager = new WebChatStreamManager(NullLogger<WebChatStreamManager>.Instance);
        _threadStateStore = new Mock<IThreadStateStore>();
        _hubNotifier = new Mock<INotifier>();
        _approvalManager = new WebChatApprovalManager(
            _streamManager,
            _hubNotifier.Object,
            NullLogger<WebChatApprovalManager>.Instance);
        _threadResolver = new ChatThreadResolver();

        _client = new WebChatMessengerClient(
            _sessionManager,
            _streamManager,
            _approvalManager,
            _threadResolver,
            _threadStateStore.Object,
            _hubNotifier.Object,
            NullLogger<WebChatMessengerClient>.Instance);
    }

    public void Dispose()
    {
        _client.Dispose();
        _streamManager.Dispose();
        _threadResolver.Dispose();
    }

    [Fact]
    public async Task CreateTopicIfNeededAsync_WithServiceBusSource_CreatesStream()
    {
        // Arrange
        _threadStateStore.Setup(s => s.GetTopicByChatIdAndThreadIdAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TopicMetadata?)null);

        _threadStateStore.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Returns(Task.CompletedTask);

        _hubNotifier.Setup(n => n.NotifyTopicChangedAsync(
                It.IsAny<TopicChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _hubNotifier.Setup(n => n.NotifyStreamChangedAsync(
                It.IsAny<StreamChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _client.CreateTopicIfNeededAsync(
            MessageSource.ServiceBus,
            chatId: 123,
            threadId: null,
            agentId: "test-agent",
            topicName: "External message");

        // Assert
        result.AgentId.ShouldBe("test-agent");

        // Verify stream was created
        var topicId = _sessionManager.GetTopicIdByChatId(result.ChatId);
        topicId.ShouldNotBeNull();
        _streamManager.IsStreaming(topicId).ShouldBeTrue();

        // Verify hub notification was sent for stream started
        _hubNotifier.Verify(n => n.NotifyStreamChangedAsync(
            It.Is<StreamChangedNotification>(s =>
                s.ChangeType == StreamChangeType.Started &&
                s.TopicId == topicId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateTopicIfNeededAsync_WithWebUiSource_DoesNotCreateStream()
    {
        // Arrange
        _threadStateStore.Setup(s => s.GetTopicByChatIdAndThreadIdAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TopicMetadata?)null);

        _threadStateStore.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Returns(Task.CompletedTask);

        _hubNotifier.Setup(n => n.NotifyTopicChangedAsync(
                It.IsAny<TopicChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _client.CreateTopicIfNeededAsync(
            MessageSource.WebUi,
            chatId: 456,
            threadId: null,
            agentId: "test-agent",
            topicName: "User message");

        // Assert
        result.AgentId.ShouldBe("test-agent");

        // Verify stream was NOT created (WebUI creates its own stream in EnqueuePromptAndGetResponses)
        var topicId = _sessionManager.GetTopicIdByChatId(result.ChatId);
        topicId.ShouldNotBeNull();
        _streamManager.IsStreaming(topicId).ShouldBeFalse();

        // Verify no stream notification was sent
        _hubNotifier.Verify(n => n.NotifyStreamChangedAsync(
            It.IsAny<StreamChangedNotification>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTopicIfNeededAsync_WithTelegramSource_CreatesStream()
    {
        // Arrange
        _threadStateStore.Setup(s => s.GetTopicByChatIdAndThreadIdAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TopicMetadata?)null);

        _threadStateStore.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Returns(Task.CompletedTask);

        _hubNotifier.Setup(n => n.NotifyTopicChangedAsync(
                It.IsAny<TopicChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _hubNotifier.Setup(n => n.NotifyStreamChangedAsync(
                It.IsAny<StreamChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _client.CreateTopicIfNeededAsync(
            MessageSource.Telegram,
            chatId: 789,
            threadId: null,
            agentId: "test-agent",
            topicName: "Telegram message");

        // Assert
        var topicId = _sessionManager.GetTopicIdByChatId(result.ChatId);
        topicId.ShouldNotBeNull();
        _streamManager.IsStreaming(topicId).ShouldBeTrue();

        _hubNotifier.Verify(n => n.NotifyStreamChangedAsync(
            It.Is<StreamChangedNotification>(s => s.ChangeType == StreamChangeType.Started),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateTopicIfNeededAsync_WithExistingTopic_ServiceBusSource_CreatesStream()
    {
        // Arrange
        var existingTopic = new TopicMetadata(
            TopicId: "existing-topic-123",
            ChatId: 100,
            ThreadId: 200,
            AgentId: "test-agent",
            Name: "Existing topic",
            CreatedAt: DateTimeOffset.UtcNow,
            LastMessageAt: null);

        _threadStateStore.Setup(s => s.GetTopicByChatIdAndThreadIdAsync(
                "test-agent", 100, 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTopic);

        _hubNotifier.Setup(n => n.NotifyStreamChangedAsync(
                It.IsAny<StreamChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _client.CreateTopicIfNeededAsync(
            MessageSource.ServiceBus,
            chatId: 100,
            threadId: 200,
            agentId: "test-agent",
            topicName: "Follow-up message");

        // Assert
        result.ChatId.ShouldBe(100);
        result.ThreadId.ShouldBe(200);

        // Verify stream was created for existing topic
        _streamManager.IsStreaming("existing-topic-123").ShouldBeTrue();

        _hubNotifier.Verify(n => n.NotifyStreamChangedAsync(
            It.Is<StreamChangedNotification>(s =>
                s.ChangeType == StreamChangeType.Started &&
                s.TopicId == "existing-topic-123"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

**Success Criteria**:
- [ ] All 4 test methods compile without errors
- [ ] Tests fail when run against current (unfixed) code
- [ ] Verification command: `dotnet test Tests/Unit --filter "FullyQualifiedName~WebChatMessengerClientTests" --no-build`

**Dependencies**: None
**Provides**: Regression tests for CreateTopicIfNeededAsync stream creation

---

### Infrastructure/Clients/Messaging/WebChatMessengerClient.cs [edit]

**Purpose**: Fix `CreateTopicIfNeededAsync` to create streams for non-WebUI sources

**TOTAL CHANGES**: 1 (replace method implementation)

**Changes**:
1. Replace `CreateTopicIfNeededAsync` method (lines 157-187) with fixed implementation that tracks topicId and creates stream for non-WebUI sources

**Implementation Details**:
- Track `topicId` through both code paths (existing topic and new topic)
- After determining the AgentKey, check if `source != MessageSource.WebUi`
- If non-WebUI source and topicId is not null: create stream, increment pending, notify WebUI
- Use `SafeAwaitAsync` for notification to prevent failures from blocking

**Migration Pattern** (before/after):
```csharp
// BEFORE (lines 157-187):
public async Task<AgentKey> CreateTopicIfNeededAsync(
    MessageSource source,
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)
{
    if (string.IsNullOrEmpty(agentId))
    {
        throw new ArgumentException("agentId is required for WebChat", nameof(agentId));
    }

    if (threadId.HasValue && chatId.HasValue)
    {
        var existingTopic = await threadStateStore.GetTopicByChatIdAndThreadIdAsync(
            agentId, chatId.Value, threadId.Value, ct);

        if (existingTopic is not null)
        {
            sessionManager.StartSession(existingTopic.TopicId, existingTopic.AgentId,
                existingTopic.ChatId, existingTopic.ThreadId);
            return new AgentKey(existingTopic.ChatId, existingTopic.ThreadId, existingTopic.AgentId);
        }
    }

    var actualChatId = chatId ?? GenerateChatId();
    var actualThreadId = await CreateThread(actualChatId, topicName ?? "Scheduled task", agentId, ct);

    return new AgentKey(actualChatId, actualThreadId, agentId);
}

// AFTER (fixed implementation):
public async Task<AgentKey> CreateTopicIfNeededAsync(
    MessageSource source,
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)
{
    if (string.IsNullOrEmpty(agentId))
    {
        throw new ArgumentException("agentId is required for WebChat", nameof(agentId));
    }

    string? topicId = null;
    long actualChatId;
    long actualThreadId;

    if (threadId.HasValue && chatId.HasValue)
    {
        var existingTopic = await threadStateStore.GetTopicByChatIdAndThreadIdAsync(
            agentId, chatId.Value, threadId.Value, ct);

        if (existingTopic is not null)
        {
            sessionManager.StartSession(existingTopic.TopicId, existingTopic.AgentId,
                existingTopic.ChatId, existingTopic.ThreadId);
            topicId = existingTopic.TopicId;
            actualChatId = existingTopic.ChatId;
            actualThreadId = existingTopic.ThreadId;
        }
        else
        {
            actualChatId = chatId.Value;
            actualThreadId = await CreateThread(actualChatId, topicName ?? "External message", agentId, ct);
            topicId = sessionManager.GetTopicIdByChatId(actualChatId);
        }
    }
    else
    {
        actualChatId = chatId ?? GenerateChatId();
        actualThreadId = await CreateThread(actualChatId, topicName ?? "External message", agentId, ct);
        topicId = sessionManager.GetTopicIdByChatId(actualChatId);
    }

    // For non-WebUI sources, create stream and notify WebUI clients
    if (source != MessageSource.WebUi && topicId is not null)
    {
        streamManager.GetOrCreateStream(topicId, topicName ?? "", null, ct);
        streamManager.TryIncrementPending(topicId);

        await hubNotifier.NotifyStreamChangedAsync(
                new StreamChangedNotification(StreamChangeType.Started, topicId), ct)
            .SafeAwaitAsync(logger, "Failed to notify stream started for topic {TopicId}", topicId);
    }

    return new AgentKey(actualChatId, actualThreadId, agentId);
}
```

**Test File**: `Tests/Unit/Infrastructure/Messaging/WebChatMessengerClientTests.cs` - Tests written BEFORE implementation:
- Test: `CreateTopicIfNeededAsync_WithServiceBusSource_CreatesStream` - Asserts: stream is created, notification sent
- Test: `CreateTopicIfNeededAsync_WithWebUiSource_DoesNotCreateStream` - Asserts: no stream, no notification
- Test: `CreateTopicIfNeededAsync_WithTelegramSource_CreatesStream` - Asserts: stream created for other external sources
- Test: `CreateTopicIfNeededAsync_WithExistingTopic_ServiceBusSource_CreatesStream` - Asserts: stream created for existing topics

**Success Criteria**:
- [ ] All 4 unit tests pass
- [ ] Existing `CompositeChatMessengerClientTests.CreateTopicIfNeededAsync_DelegatesToFirstClient` still passes
- [ ] No build errors or warnings
- [ ] Verification command: `dotnet test Tests/Unit --filter "FullyQualifiedName~CreateTopicIfNeededAsync" --no-build`

**Dependencies**: `Tests/Unit/Infrastructure/Messaging/WebChatMessengerClientTests.cs`
**Provides**: Fixed `CreateTopicIfNeededAsync` with stream creation for non-WebUI sources

**Rationale**: This fix follows the existing pattern from `StartScheduledStreamAsync` (lines 191-206) which correctly creates streams. The change is minimal and targeted - only adding the necessary stream creation logic for non-WebUI sources while preserving all existing behavior.

**Regression Prevention**: The unit tests explicitly verify:
1. ServiceBus source creates stream (main bug fix)
2. WebUI source does NOT create stream (preserves existing behavior)
3. Other external sources (Telegram) also create stream (consistent behavior)
4. Existing topics also get streams created (edge case coverage)

## Dependency Graph

> Files in the same phase can execute in parallel.

| Phase | File | Action | Depends On |
|-------|------|--------|------------|
| 1 | `Tests/Unit/Infrastructure/Messaging/WebChatMessengerClientTests.cs` | create | - |
| 2 | `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs` | edit | `Tests/Unit/Infrastructure/Messaging/WebChatMessengerClientTests.cs` |

## Exit Criteria

### Test Commands
```bash
dotnet build                                    # Build entire solution
dotnet test Tests/Unit --filter "FullyQualifiedName~WebChatMessengerClientTests"  # Run new tests
dotnet test Tests/Unit                          # Run all unit tests
```

### Success Conditions
- [ ] Bug is fixed (original warning no longer occurs for ServiceBus messages)
- [ ] Regression tests added and passing (4 new tests)
- [ ] All existing tests pass (exit code 0)
- [ ] No build errors or warnings (exit code 0)
- [ ] All requirements satisfied:
  - [ ] R1: ServiceBus messages stream to WebUI
  - [ ] R2: Each ServiceBus sourceId maps to WebUI topic
  - [ ] R3: WebUI clients notified when ServiceBus conversation starts
  - [ ] R4: WebUI-originated flow unchanged
- [ ] All files implemented

### Verification Script
```bash
dotnet build && dotnet test Tests/Unit --filter "FullyQualifiedName~WebChatMessengerClientTests" && dotnet test Tests/Unit
```

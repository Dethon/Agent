# Message Pipeline Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Unify all five message sources through a single MessagePipeline service that owns deduplication and accumulation.

**Architecture:** Introduce `IMessagePipeline` as the single entry point for all message operations. Sources call the pipeline instead of dispatching actions directly. The pipeline handles deduplication, accumulation, and state transitions, then dispatches simplified actions to stores.

**Tech Stack:** C# 10, Blazor WebAssembly, Redux-like state management, xUnit + Shouldly for tests.

---

## Task 1: Create Pipeline Types

**Files:**
- Create: `WebChat.Client/State/Pipeline/MessageLifecycle.cs`
- Create: `WebChat.Client/State/Pipeline/ManagedMessage.cs`
- Create: `WebChat.Client/State/Pipeline/MessageLifecycleEvent.cs`
- Create: `WebChat.Client/State/Pipeline/PipelineSnapshot.cs`
- Test: `Tests/Unit/WebChat.Client/State/Pipeline/ManagedMessageTests.cs`

**Step 1: Write the failing test**

```csharp
// Tests/Unit/WebChat.Client/State/Pipeline/ManagedMessageTests.cs
using Shouldly;
using WebChat.Client.State.Pipeline;

namespace Tests.Unit.WebChat.Client.State.Pipeline;

public sealed class ManagedMessageTests
{
    [Fact]
    public void ManagedMessage_HasContent_WhenContentNotEmpty()
    {
        var message = new ManagedMessage
        {
            Id = "msg-1",
            TopicId = "topic-1",
            State = MessageLifecycle.Streaming,
            Role = "assistant",
            Content = "Hello"
        };

        message.HasContent.ShouldBeTrue();
    }

    [Fact]
    public void ManagedMessage_HasContent_WhenReasoningNotEmpty()
    {
        var message = new ManagedMessage
        {
            Id = "msg-1",
            TopicId = "topic-1",
            State = MessageLifecycle.Streaming,
            Role = "assistant",
            Reasoning = "Thinking..."
        };

        message.HasContent.ShouldBeTrue();
    }

    [Fact]
    public void ManagedMessage_HasNoContent_WhenAllEmpty()
    {
        var message = new ManagedMessage
        {
            Id = "msg-1",
            TopicId = "topic-1",
            State = MessageLifecycle.Pending,
            Role = "user"
        };

        message.HasContent.ShouldBeFalse();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~ManagedMessageTests" -v n`
Expected: FAIL - types don't exist

**Step 3: Write the types**

```csharp
// WebChat.Client/State/Pipeline/MessageLifecycle.cs
namespace WebChat.Client.State.Pipeline;

public enum MessageLifecycle
{
    Pending,      // User message created, awaiting server confirmation
    Streaming,    // Assistant message receiving chunks
    Finalized     // Complete, in history
}
```

```csharp
// WebChat.Client/State/Pipeline/ManagedMessage.cs
namespace WebChat.Client.State.Pipeline;

public sealed record ManagedMessage
{
    public required string Id { get; init; }
    public required string TopicId { get; init; }
    public required MessageLifecycle State { get; init; }
    public required string Role { get; init; }
    public string Content { get; init; } = "";
    public string? Reasoning { get; init; }
    public string? ToolCalls { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? SenderId { get; init; }

    public bool HasContent =>
        !string.IsNullOrEmpty(Content) ||
        !string.IsNullOrEmpty(Reasoning) ||
        !string.IsNullOrEmpty(ToolCalls);
}
```

```csharp
// WebChat.Client/State/Pipeline/MessageLifecycleEvent.cs
namespace WebChat.Client.State.Pipeline;

public sealed record MessageLifecycleEvent(
    string TopicId,
    string MessageId,
    MessageLifecycle FromState,
    MessageLifecycle ToState,
    string Source,
    DateTimeOffset Timestamp);
```

```csharp
// WebChat.Client/State/Pipeline/PipelineSnapshot.cs
namespace WebChat.Client.State.Pipeline;

public sealed record PipelineSnapshot(
    string? StreamingMessageId,
    int FinalizedCount,
    int PendingUserMessages,
    IReadOnlyList<ManagedMessage> ActiveMessages);
```

**Step 4: Run test to verify it passes**

Run: `dotnet test Tests --filter "FullyQualifiedName~ManagedMessageTests" -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add WebChat.Client/State/Pipeline/ Tests/Unit/WebChat.Client/State/Pipeline/
git commit -m "feat(webchat): add message pipeline types"
```

---

## Task 2: Create Pipeline Interface and Basic Implementation

**Files:**
- Create: `WebChat.Client/State/Pipeline/IMessagePipeline.cs`
- Create: `WebChat.Client/State/Pipeline/MessagePipeline.cs`
- Test: `Tests/Unit/WebChat.Client/State/Pipeline/MessagePipelineTests.cs`

**Step 1: Write the failing test**

```csharp
// Tests/Unit/WebChat.Client/State/Pipeline/MessagePipelineTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;

namespace Tests.Unit.WebChat.Client.State.Pipeline;

public sealed class MessagePipelineTests
{
    private readonly Dispatcher _dispatcher;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly MessagePipeline _pipeline;

    public MessagePipelineTests()
    {
        _dispatcher = new Dispatcher();
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _pipeline = new MessagePipeline(
            _dispatcher,
            _messagesStore,
            _streamingStore,
            NullLogger<MessagePipeline>.Instance);
    }

    [Fact]
    public void SubmitUserMessage_ReturnsCorrelationId()
    {
        var id = _pipeline.SubmitUserMessage("topic-1", "Hello", "user-1");

        id.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void SubmitUserMessage_DispatchesAddMessage()
    {
        _pipeline.SubmitUserMessage("topic-1", "Hello", "user-1");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault("topic-1");
        messages.ShouldNotBeNull();
        messages.Count.ShouldBe(1);
        messages[0].Role.ShouldBe("user");
        messages[0].Content.ShouldBe("Hello");
        messages[0].SenderId.ShouldBe("user-1");
    }

    [Fact]
    public void SubmitUserMessage_TracksAsPending()
    {
        var id = _pipeline.SubmitUserMessage("topic-1", "Hello", "user-1");

        var snapshot = _pipeline.GetSnapshot("topic-1");
        snapshot.PendingUserMessages.ShouldBe(1);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~MessagePipelineTests" -v n`
Expected: FAIL - IMessagePipeline and MessagePipeline don't exist

**Step 3: Write the interface**

```csharp
// WebChat.Client/State/Pipeline/IMessagePipeline.cs
using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Pipeline;

public interface IMessagePipeline
{
    /// <summary>User sends a message. Returns correlation ID for tracking.</summary>
    string SubmitUserMessage(string topicId, string content, string? senderId);

    /// <summary>Streaming chunk arrives from any source.</summary>
    void AccumulateChunk(string topicId, string? messageId,
        string? content, string? reasoning, string? toolCalls);

    /// <summary>Message complete (turn ended or stream finished).</summary>
    void FinalizeMessage(string topicId, string? messageId);

    /// <summary>Load history from server.</summary>
    void LoadHistory(string topicId, IEnumerable<ChatHistoryMessage> messages);

    /// <summary>Resume from buffered messages after reconnection.</summary>
    void ResumeFromBuffer(string topicId, IReadOnlyList<ChatStreamMessage> buffer,
        string? currentMessageId, string? currentPrompt, string? currentSenderId);

    /// <summary>Reset pipeline state for topic (error or cancel).</summary>
    void Reset(string topicId);

    /// <summary>Check if a correlation ID was sent by this client.</summary>
    bool WasSentByThisClient(string? correlationId);

    /// <summary>Get debug snapshot of pipeline state.</summary>
    PipelineSnapshot GetSnapshot(string topicId);

    /// <summary>Lifecycle events for debugging.</summary>
    IObservable<MessageLifecycleEvent> LifecycleEvents { get; }
}
```

**Step 4: Write the basic implementation**

```csharp
// WebChat.Client/State/Pipeline/MessagePipeline.cs
using System.Reactive.Subjects;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging;
using WebChat.Client.Models;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;

namespace WebChat.Client.State.Pipeline;

public sealed class MessagePipeline : IMessagePipeline, IDisposable
{
    private readonly IDispatcher _dispatcher;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly ILogger<MessagePipeline> _logger;

    private readonly Dictionary<string, ManagedMessage> _messagesById = new();
    private readonly Dictionary<string, HashSet<string>> _finalizedByTopic = new();
    private readonly Dictionary<string, string> _pendingUserMessages = new();
    private readonly Dictionary<string, string> _streamingByTopic = new();
    private readonly Subject<MessageLifecycleEvent> _lifecycleEvents = new();
    private readonly object _lock = new();

    public IObservable<MessageLifecycleEvent> LifecycleEvents => _lifecycleEvents;

    public MessagePipeline(
        IDispatcher dispatcher,
        MessagesStore messagesStore,
        StreamingStore streamingStore,
        ILogger<MessagePipeline> logger)
    {
        _dispatcher = dispatcher;
        _messagesStore = messagesStore;
        _streamingStore = streamingStore;
        _logger = logger;
    }

    public string SubmitUserMessage(string topicId, string content, string? senderId)
    {
        var correlationId = Guid.NewGuid().ToString("N");

        lock (_lock)
        {
            _pendingUserMessages[correlationId] = topicId;

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Pipeline.SubmitUserMessage: topic={TopicId}, correlationId={CorrelationId}, senderId={SenderId}",
                    topicId, correlationId, senderId);
            }
        }

        _dispatcher.Dispatch(new AddMessage(topicId, new ChatMessageModel
        {
            Role = "user",
            Content = content,
            SenderId = senderId,
            Timestamp = DateTimeOffset.UtcNow
        }));

        return correlationId;
    }

    public void AccumulateChunk(string topicId, string? messageId,
        string? content, string? reasoning, string? toolCalls)
    {
        lock (_lock)
        {
            if (!ShouldProcess(topicId, messageId))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Pipeline.AccumulateChunk: SKIPPED (already finalized) topic={TopicId}, messageId={MessageId}",
                        topicId, messageId);
                }
                return;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Pipeline.AccumulateChunk: topic={TopicId}, messageId={MessageId}, contentLen={ContentLen}",
                    topicId, messageId, content?.Length ?? 0);
            }
        }

        // Dispatch StreamChunk - this still uses the existing reducer for now
        // Phase 3 will simplify this
        _dispatcher.Dispatch(new StreamChunk(topicId, content, reasoning, toolCalls, messageId));
    }

    public void FinalizeMessage(string topicId, string? messageId)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(messageId))
            {
                if (!_finalizedByTopic.TryGetValue(topicId, out var finalized))
                {
                    finalized = new HashSet<string>();
                    _finalizedByTopic[topicId] = finalized;
                }

                if (finalized.Contains(messageId))
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(
                            "Pipeline.FinalizeMessage: SKIPPED (already finalized) topic={TopicId}, messageId={MessageId}",
                            topicId, messageId);
                    }
                    return;
                }

                finalized.Add(messageId);
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Pipeline.FinalizeMessage: topic={TopicId}, messageId={MessageId}",
                    topicId, messageId);
            }
        }

        // Get current streaming content and add as message
        var streamingContent = _streamingStore.State.StreamingByTopic.GetValueOrDefault(topicId);
        if (streamingContent?.HasContent == true)
        {
            _dispatcher.Dispatch(new AddMessage(
                topicId,
                new ChatMessageModel
                {
                    Role = "assistant",
                    Content = streamingContent.Content,
                    Reasoning = streamingContent.Reasoning,
                    ToolCalls = streamingContent.ToolCalls,
                    MessageId = messageId
                },
                messageId));

            _dispatcher.Dispatch(new ResetStreamingContent(topicId));
        }
    }

    public void LoadHistory(string topicId, IEnumerable<ChatHistoryMessage> messages)
    {
        var chatMessages = messages.Select(h => new ChatMessageModel
        {
            Role = h.Role,
            Content = h.Content,
            Reasoning = h.Reasoning,
            ToolCalls = h.ToolCalls,
            MessageId = h.MessageId,
            SenderId = h.SenderId,
            Timestamp = h.Timestamp
        }).ToList();

        lock (_lock)
        {
            // Track all message IDs as finalized
            if (!_finalizedByTopic.TryGetValue(topicId, out var finalized))
            {
                finalized = new HashSet<string>();
                _finalizedByTopic[topicId] = finalized;
            }

            foreach (var msg in chatMessages.Where(m => !string.IsNullOrEmpty(m.MessageId)))
            {
                finalized.Add(msg.MessageId!);
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Pipeline.LoadHistory: topic={TopicId}, count={Count}, finalizedIds={FinalizedCount}",
                    topicId, chatMessages.Count, finalized.Count);
            }
        }

        _dispatcher.Dispatch(new MessagesLoaded(topicId, chatMessages));
    }

    public void ResumeFromBuffer(string topicId, IReadOnlyList<ChatStreamMessage> buffer,
        string? currentMessageId, string? currentPrompt, string? currentSenderId)
    {
        // Delegate to existing BufferRebuildUtility for now
        // This maintains compatibility while migrating
        var existingMessages = _messagesStore.State.MessagesByTopic
            .GetValueOrDefault(topicId) ?? [];

        var historyContent = existingMessages
            .Where(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Content))
            .Select(m => m.Content)
            .ToHashSet();

        var (completedTurns, streamingMessage) =
            Services.Streaming.BufferRebuildUtility.RebuildFromBuffer(buffer, historyContent);

        lock (_lock)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Pipeline.ResumeFromBuffer: topic={TopicId}, bufferCount={BufferCount}, " +
                    "completedTurns={CompletedTurns}, hasStreamingContent={HasStreaming}",
                    topicId, buffer.Count, completedTurns.Count, streamingMessage.HasContent);
            }
        }

        // Add current prompt if not already present
        if (!string.IsNullOrEmpty(currentPrompt))
        {
            var promptExists = existingMessages.Any(m =>
                m.Role == "user" && m.Content == currentPrompt);

            if (!promptExists)
            {
                _dispatcher.Dispatch(new AddMessage(topicId, new ChatMessageModel
                {
                    Role = "user",
                    Content = currentPrompt,
                    SenderId = currentSenderId
                }));
            }
        }

        // Add completed turns (skip user messages matching currentPrompt)
        foreach (var turn in completedTurns.Where(t =>
            t.HasContent && !(t.Role == "user" && t.Content == currentPrompt)))
        {
            _dispatcher.Dispatch(new AddMessage(topicId, turn));
        }

        // Dispatch streaming content
        if (streamingMessage.HasContent)
        {
            _dispatcher.Dispatch(new StreamChunk(
                topicId,
                streamingMessage.Content,
                streamingMessage.Reasoning,
                streamingMessage.ToolCalls,
                currentMessageId));
        }
    }

    public void Reset(string topicId)
    {
        lock (_lock)
        {
            _streamingByTopic.Remove(topicId);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Pipeline.Reset: topic={TopicId}", topicId);
            }
        }

        _dispatcher.Dispatch(new ResetStreamingContent(topicId));
    }

    public bool WasSentByThisClient(string? correlationId)
    {
        if (string.IsNullOrEmpty(correlationId))
            return false;

        lock (_lock)
        {
            return _pendingUserMessages.ContainsKey(correlationId);
        }
    }

    public PipelineSnapshot GetSnapshot(string topicId)
    {
        lock (_lock)
        {
            var streamingId = _streamingByTopic.GetValueOrDefault(topicId);
            var finalizedCount = _finalizedByTopic.GetValueOrDefault(topicId)?.Count ?? 0;
            var pendingCount = _pendingUserMessages.Count;
            var activeMessages = _messagesById.Values
                .Where(m => m.TopicId == topicId)
                .ToList();

            return new PipelineSnapshot(streamingId, finalizedCount, pendingCount, activeMessages);
        }
    }

    private bool ShouldProcess(string topicId, string? messageId)
    {
        if (string.IsNullOrEmpty(messageId))
            return true;

        if (_finalizedByTopic.TryGetValue(topicId, out var finalized) &&
            finalized.Contains(messageId))
            return false;

        return true;
    }

    public void Dispose()
    {
        _lifecycleEvents.Dispose();
    }
}
```

**Step 5: Run test to verify it passes**

Run: `dotnet test Tests --filter "FullyQualifiedName~MessagePipelineTests" -v n`
Expected: PASS

**Step 6: Commit**

```bash
git add WebChat.Client/State/Pipeline/ Tests/Unit/WebChat.Client/State/Pipeline/
git commit -m "feat(webchat): add MessagePipeline interface and basic implementation"
```

---

## Task 3: Add Pipeline Tests for Deduplication

**Files:**
- Modify: `Tests/Unit/WebChat.Client/State/Pipeline/MessagePipelineTests.cs`

**Step 1: Write the failing tests**

Add to `MessagePipelineTests.cs`:

```csharp
[Fact]
public void AccumulateChunk_SkipsDuplicateAfterFinalize()
{
    _pipeline.AccumulateChunk("topic-1", "msg-1", "Hello", null, null);
    _pipeline.FinalizeMessage("topic-1", "msg-1");

    // This should be skipped
    _pipeline.AccumulateChunk("topic-1", "msg-1", " duplicate", null, null);

    var snapshot = _pipeline.GetSnapshot("topic-1");
    snapshot.FinalizedCount.ShouldBe(1);
}

[Fact]
public void FinalizeMessage_SkipsSecondFinalize()
{
    // Start streaming
    _dispatcher.Dispatch(new StreamStarted("topic-1"));
    _pipeline.AccumulateChunk("topic-1", "msg-1", "Content", null, null);

    // First finalize
    _pipeline.FinalizeMessage("topic-1", "msg-1");
    var countAfterFirst = _messagesStore.State.MessagesByTopic
        .GetValueOrDefault("topic-1")?.Count ?? 0;

    // Second finalize should skip
    _pipeline.FinalizeMessage("topic-1", "msg-1");
    var countAfterSecond = _messagesStore.State.MessagesByTopic
        .GetValueOrDefault("topic-1")?.Count ?? 0;

    countAfterFirst.ShouldBe(1);
    countAfterSecond.ShouldBe(1);
}

[Fact]
public void WasSentByThisClient_ReturnsTrueForTrackedCorrelationId()
{
    var correlationId = _pipeline.SubmitUserMessage("topic-1", "Hello", "user-1");

    _pipeline.WasSentByThisClient(correlationId).ShouldBeTrue();
}

[Fact]
public void WasSentByThisClient_ReturnsFalseForUnknownCorrelationId()
{
    _pipeline.WasSentByThisClient("unknown-id").ShouldBeFalse();
}

[Fact]
public void WasSentByThisClient_ReturnsFalseForNull()
{
    _pipeline.WasSentByThisClient(null).ShouldBeFalse();
}

[Fact]
public void LoadHistory_TracksFinalizedMessageIds()
{
    var history = new List<ChatHistoryMessage>
    {
        new() { MessageId = "msg-1", Role = "assistant", Content = "Hello" },
        new() { MessageId = "msg-2", Role = "assistant", Content = "World" }
    };

    _pipeline.LoadHistory("topic-1", history);

    var snapshot = _pipeline.GetSnapshot("topic-1");
    snapshot.FinalizedCount.ShouldBe(2);
}

[Fact]
public void LoadHistory_DispatchesMessagesLoaded()
{
    var history = new List<ChatHistoryMessage>
    {
        new() { MessageId = "msg-1", Role = "user", Content = "Hello" },
        new() { MessageId = "msg-2", Role = "assistant", Content = "Hi there" }
    };

    _pipeline.LoadHistory("topic-1", history);

    var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault("topic-1");
    messages.ShouldNotBeNull();
    messages.Count.ShouldBe(2);
}
```

**Step 2: Run test to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~MessagePipelineTests" -v n`
Expected: PASS (tests should pass with current implementation)

**Step 3: Commit**

```bash
git add Tests/Unit/WebChat.Client/State/Pipeline/
git commit -m "test(webchat): add deduplication tests for MessagePipeline"
```

---

## Task 4: Register Pipeline in DI

**Files:**
- Modify: `WebChat.Client/Program.cs`

**Step 1: Read current Program.cs**

Read the file to find where services are registered.

**Step 2: Add pipeline registration**

Add to the service registration section:

```csharp
builder.Services.AddSingleton<IMessagePipeline, MessagePipeline>();
```

**Step 3: Build to verify**

Run: `dotnet build WebChat.Client`
Expected: SUCCESS

**Step 4: Commit**

```bash
git add WebChat.Client/Program.cs
git commit -m "feat(webchat): register MessagePipeline in DI"
```

---

## Task 5: Migrate SendMessageEffect to Pipeline

**Files:**
- Modify: `WebChat.Client/State/Effects/SendMessageEffect.cs`
- Test: `Tests/Unit/WebChat.Client/State/SendMessageEffectTests.cs` (create if needed)

**Step 1: Update SendMessageEffect constructor**

Replace `SentMessageTracker` with `IMessagePipeline`:

```csharp
public sealed class SendMessageEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly StreamingStore _streamingStore;
    private readonly IChatSessionService _sessionService;
    private readonly IStreamingService _streamingService;
    private readonly ITopicService _topicService;
    private readonly IChatMessagingService _messagingService;
    private readonly UserIdentityStore _userIdentityStore;
    private readonly IMessagePipeline _pipeline;

    public SendMessageEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        StreamingStore streamingStore,
        IChatSessionService sessionService,
        IStreamingService streamingService,
        ITopicService topicService,
        IChatMessagingService messagingService,
        UserIdentityStore userIdentityStore,
        IMessagePipeline pipeline)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _streamingStore = streamingStore;
        _sessionService = sessionService;
        _streamingService = streamingService;
        _topicService = topicService;
        _messagingService = messagingService;
        _userIdentityStore = userIdentityStore;
        _pipeline = pipeline;

        dispatcher.RegisterHandler<SendMessage>(HandleSendMessage);
        dispatcher.RegisterHandler<CancelStreaming>(HandleCancelStreaming);
    }
```

**Step 2: Update HandleSendMessageAsync**

Replace the correlation ID and AddMessage logic:

```csharp
private async Task HandleSendMessageAsync(SendMessage action)
{
    var state = _topicsStore.State;
    StoredTopic topic;

    if (string.IsNullOrEmpty(action.TopicId))
    {
        // Create new topic (unchanged)
        var topicName = action.Message.Length > 50 ? action.Message[..50] + "..." : action.Message;
        var topicId = TopicIdGenerator.GenerateTopicId();
        topic = new StoredTopic
        {
            TopicId = topicId,
            ChatId = TopicIdGenerator.GetChatIdForTopic(topicId),
            ThreadId = TopicIdGenerator.GetThreadIdForTopic(topicId),
            AgentId = state.SelectedAgentId!,
            Name = topicName,
            CreatedAt = DateTime.UtcNow
        };

        var success = await _sessionService.StartSessionAsync(topic);
        if (!success)
        {
            return;
        }

        _dispatcher.Dispatch(new AddTopic(topic));
        _dispatcher.Dispatch(new SelectTopic(topic.TopicId));
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        await _topicService.SaveTopicAsync(topic.ToMetadata(), isNew: true);
    }
    else
    {
        topic = state.Topics.First(t => t.TopicId == action.TopicId);
        if (_sessionService.CurrentTopic?.TopicId != topic.TopicId)
        {
            await _sessionService.StartSessionAsync(topic);
        }
    }

    // If streaming is active, finalize via pipeline
    var streamingState = _streamingStore.State;
    if (streamingState.StreamingTopics.Contains(topic.TopicId))
    {
        var currentContent = streamingState.StreamingByTopic.GetValueOrDefault(topic.TopicId);
        if (currentContent?.HasContent == true)
        {
            _pipeline.FinalizeMessage(topic.TopicId, currentContent.CurrentMessageId);
            _dispatcher.Dispatch(new RequestContentFinalization(topic.TopicId));
        }
    }

    // Submit user message via pipeline
    var identityState = _userIdentityStore.State;
    var currentUser = identityState.AvailableUsers
        .FirstOrDefault(u => u.Id == identityState.SelectedUserId);

    var correlationId = _pipeline.SubmitUserMessage(
        topic.TopicId,
        action.Message,
        currentUser?.Id);

    // Delegate to streaming service
    _ = _streamingService.SendMessageAsync(topic, action.Message, correlationId);
}
```

**Step 3: Add using statement**

```csharp
using WebChat.Client.State.Pipeline;
```

**Step 4: Build to verify**

Run: `dotnet build WebChat.Client`
Expected: SUCCESS

**Step 5: Commit**

```bash
git add WebChat.Client/State/Effects/SendMessageEffect.cs
git commit -m "refactor(webchat): migrate SendMessageEffect to use MessagePipeline"
```

---

## Task 6: Migrate HubEventDispatcher to Pipeline

**Files:**
- Modify: `WebChat.Client/State/Hub/HubEventDispatcher.cs`

**Step 1: Update constructor**

Replace `SentMessageTracker` with `IMessagePipeline`:

```csharp
public sealed class HubEventDispatcher(
    IDispatcher dispatcher,
    TopicsStore topicsStore,
    StreamingStore streamingStore,
    IMessagePipeline pipeline,
    IStreamResumeService streamResumeService) : IHubEventDispatcher
```

**Step 2: Update HandleUserMessage**

```csharp
public void HandleUserMessage(UserMessageNotification notification)
{
    // Only add if we're watching this topic
    var currentTopic = topicsStore.State.SelectedTopicId;
    if (currentTopic != notification.TopicId)
    {
        return;
    }

    // Skip if this message was sent by this browser instance
    if (pipeline.WasSentByThisClient(notification.CorrelationId))
    {
        return;
    }

    // If streaming is active, finalize current assistant content via pipeline
    var streamingState = streamingStore.State;
    if (streamingState.StreamingTopics.Contains(notification.TopicId))
    {
        var currentContent = streamingState.StreamingByTopic.GetValueOrDefault(notification.TopicId);
        if (currentContent?.HasContent == true)
        {
            pipeline.FinalizeMessage(notification.TopicId, currentContent.CurrentMessageId);
            dispatcher.Dispatch(new RequestContentFinalization(notification.TopicId));
        }
    }

    // Add the user message
    dispatcher.Dispatch(new AddMessage(notification.TopicId, new ChatMessageModel
    {
        Role = "user",
        Content = notification.Content,
        SenderId = notification.SenderId,
        Timestamp = notification.Timestamp
    }));
}
```

**Step 3: Add using statement**

```csharp
using WebChat.Client.State.Pipeline;
```

**Step 4: Build to verify**

Run: `dotnet build WebChat.Client`
Expected: SUCCESS

**Step 5: Commit**

```bash
git add WebChat.Client/State/Hub/HubEventDispatcher.cs
git commit -m "refactor(webchat): migrate HubEventDispatcher to use MessagePipeline"
```

---

## Task 7: Migrate TopicSelectionEffect to Pipeline

**Files:**
- Modify: `WebChat.Client/State/Effects/TopicSelectionEffect.cs`

**Step 1: Update constructor**

Add `IMessagePipeline` dependency:

```csharp
public sealed class TopicSelectionEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly IChatSessionService _sessionService;
    private readonly ITopicService _topicService;
    private readonly IStreamResumeService _streamResumeService;
    private readonly IMessagePipeline _pipeline;

    public TopicSelectionEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        MessagesStore messagesStore,
        IChatSessionService sessionService,
        ITopicService topicService,
        IStreamResumeService streamResumeService,
        IMessagePipeline pipeline)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _messagesStore = messagesStore;
        _sessionService = sessionService;
        _topicService = topicService;
        _streamResumeService = streamResumeService;
        _pipeline = pipeline;

        dispatcher.RegisterHandler<SelectTopic>(HandleSelectTopic);
    }
```

**Step 2: Update HandleSelectTopicAsync**

```csharp
private async Task HandleSelectTopicAsync(string topicId)
{
    var topic = _topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId);
    if (topic is null)
    {
        return;
    }

    // Check if messages already loaded
    var hasMessages = _messagesStore.State.MessagesByTopic.ContainsKey(topicId);
    if (!hasMessages)
    {
        await _sessionService.StartSessionAsync(topic);
        var history = await _topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);

        // Re-check after async work - SendMessageEffect might have added messages
        var currentMessages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topicId, []);
        if (currentMessages.Count == 0)
        {
            _pipeline.LoadHistory(topicId, history);
        }
    }

    // Mark messages as read (unchanged)
    await MarkTopicAsReadAsync(topic);

    // Try to resume any active streaming
    _ = _streamResumeService.TryResumeStreamAsync(topic);
}
```

**Step 3: Add using statement**

```csharp
using WebChat.Client.State.Pipeline;
```

**Step 4: Build to verify**

Run: `dotnet build WebChat.Client`
Expected: SUCCESS

**Step 5: Commit**

```bash
git add WebChat.Client/State/Effects/TopicSelectionEffect.cs
git commit -m "refactor(webchat): migrate TopicSelectionEffect to use MessagePipeline"
```

---

## Task 8: Migrate InitializationEffect to Pipeline

**Files:**
- Modify: `WebChat.Client/State/Effects/InitializationEffect.cs`

**Step 1: Update constructor**

Add `IMessagePipeline` dependency:

```csharp
public sealed class InitializationEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IChatConnectionService _connectionService;
    private readonly IAgentService _agentService;
    private readonly ITopicService _topicService;
    private readonly ILocalStorageService _localStorage;
    private readonly ISignalREventSubscriber _eventSubscriber;
    private readonly IStreamResumeService _streamResumeService;
    private readonly UserIdentityStore _userIdentityStore;
    private readonly IMessagePipeline _pipeline;

    public InitializationEffect(
        Dispatcher dispatcher,
        IChatConnectionService connectionService,
        IAgentService agentService,
        ITopicService topicService,
        ILocalStorageService localStorage,
        ISignalREventSubscriber eventSubscriber,
        IStreamResumeService streamResumeService,
        UserIdentityStore userIdentityStore,
        IMessagePipeline pipeline)
    {
        _dispatcher = dispatcher;
        _connectionService = connectionService;
        _agentService = agentService;
        _topicService = topicService;
        _localStorage = localStorage;
        _eventSubscriber = eventSubscriber;
        _streamResumeService = streamResumeService;
        _userIdentityStore = userIdentityStore;
        _pipeline = pipeline;

        dispatcher.RegisterHandler<Initialize>(HandleInitialize);
        dispatcher.RegisterHandler<SelectUser>(HandleSelectUser);
    }
```

**Step 2: Update LoadTopicHistoryAsync**

```csharp
private async Task LoadTopicHistoryAsync(StoredTopic topic)
{
    var history = await _topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);
    _pipeline.LoadHistory(topic.TopicId, history);

    _ = _streamResumeService.TryResumeStreamAsync(topic);
}
```

**Step 3: Add using statement**

```csharp
using WebChat.Client.State.Pipeline;
```

**Step 4: Build to verify**

Run: `dotnet build WebChat.Client`
Expected: SUCCESS

**Step 5: Commit**

```bash
git add WebChat.Client/State/Effects/InitializationEffect.cs
git commit -m "refactor(webchat): migrate InitializationEffect to use MessagePipeline"
```

---

## Task 9: Migrate StreamResumeService to Pipeline

**Files:**
- Modify: `WebChat.Client/Services/Streaming/StreamResumeService.cs`

**Step 1: Update constructor**

Replace `MessagesStore` with `IMessagePipeline`:

```csharp
public sealed class StreamResumeService(
    IChatMessagingService messagingService,
    ITopicService topicService,
    IApprovalService approvalService,
    IStreamingService streamingService,
    IDispatcher dispatcher,
    IMessagePipeline pipeline,
    StreamingStore streamingStore) : IStreamResumeService
```

**Step 2: Update TryResumeStreamAsync**

```csharp
public async Task TryResumeStreamAsync(StoredTopic topic)
{
    // Check if already resuming via store state
    if (streamingStore.State.ResumingTopics.Contains(topic.TopicId))
    {
        return;
    }

    dispatcher.Dispatch(new StartResuming(topic.TopicId));

    try
    {
        // Check if topic is already streaming via store (quick check before server call)
        if (streamingStore.State.StreamingTopics.Contains(topic.TopicId))
        {
            return;
        }

        // Check if streaming service has an active stream (atomic check with lock)
        if (await streamingService.IsStreamActiveAsync(topic.TopicId))
        {
            return;
        }

        var state = await messagingService.GetStreamStateAsync(topic.TopicId);
        if (state is null || state is { IsProcessing: false, BufferedMessages.Count: 0 })
        {
            return;
        }

        var pendingApproval = await approvalService.GetPendingApprovalForTopicAsync(topic.TopicId);
        if (pendingApproval is not null)
        {
            dispatcher.Dispatch(new ShowApproval(topic.TopicId, pendingApproval));
        }

        // Use pipeline to handle buffer resume
        pipeline.ResumeFromBuffer(
            topic.TopicId,
            state.BufferedMessages,
            state.CurrentMessageId,
            state.CurrentPrompt,
            state.CurrentSenderId);

        // Build streaming message for resume stream
        var existingMessages = state.BufferedMessages;
        var historyContent = new HashSet<string>();  // Pipeline handles this internally

        var (_, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(existingMessages, historyContent);

        // Use TryStartResumeStreamAsync to atomically check and start the stream
        await streamingService.TryStartResumeStreamAsync(topic, streamingMessage, state.CurrentMessageId);
    }
    finally
    {
        dispatcher.Dispatch(new StopResuming(topic.TopicId));
    }
}
```

**Step 3: Add using statement**

```csharp
using WebChat.Client.State.Pipeline;
```

**Step 4: Build to verify**

Run: `dotnet build WebChat.Client`
Expected: SUCCESS

**Step 5: Commit**

```bash
git add WebChat.Client/Services/Streaming/StreamResumeService.cs
git commit -m "refactor(webchat): migrate StreamResumeService to use MessagePipeline"
```

---

## Task 10: Delete SentMessageTracker

**Files:**
- Delete: `WebChat.Client/Services/SentMessageTracker.cs`
- Modify: `WebChat.Client/Program.cs` - remove registration
- Modify: Any remaining references

**Step 1: Search for remaining usages**

Run: `grep -r "SentMessageTracker" --include="*.cs"`

**Step 2: Remove DI registration from Program.cs**

Remove the line:
```csharp
builder.Services.AddSingleton<SentMessageTracker>();
```

**Step 3: Delete the file**

```bash
rm WebChat.Client/Services/SentMessageTracker.cs
```

**Step 4: Build to verify**

Run: `dotnet build WebChat.Client`
Expected: SUCCESS

**Step 5: Run all tests**

Run: `dotnet test Tests -v n`
Expected: PASS

**Step 6: Commit**

```bash
git add -A
git commit -m "refactor(webchat): remove SentMessageTracker (replaced by MessagePipeline)"
```

---

## Task 11: Update HubEventDispatcher Tests

**Files:**
- Modify: `Tests/Unit/WebChat.Client/State/HubEventDispatcherTests.cs`

**Step 1: Read current tests**

Read the file to understand current test structure.

**Step 2: Update tests to use pipeline mock**

Update the test setup to inject `IMessagePipeline` mock instead of `SentMessageTracker`.

**Step 3: Run tests**

Run: `dotnet test Tests --filter "FullyQualifiedName~HubEventDispatcherTests" -v n`
Expected: PASS

**Step 4: Commit**

```bash
git add Tests/Unit/WebChat.Client/State/HubEventDispatcherTests.cs
git commit -m "test(webchat): update HubEventDispatcher tests for MessagePipeline"
```

---

## Task 12: Add Integration Test for Full Pipeline Flow

**Files:**
- Create: `Tests/Unit/WebChat.Client/State/Pipeline/MessagePipelineIntegrationTests.cs`

**Step 1: Write integration test**

```csharp
using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;

namespace Tests.Unit.WebChat.Client.State.Pipeline;

public sealed class MessagePipelineIntegrationTests
{
    private readonly Dispatcher _dispatcher;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly MessagePipeline _pipeline;

    public MessagePipelineIntegrationTests()
    {
        _dispatcher = new Dispatcher();
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _pipeline = new MessagePipeline(
            _dispatcher,
            _messagesStore,
            _streamingStore,
            NullLogger<MessagePipeline>.Instance);
    }

    [Fact]
    public void FullConversationFlow_UserSendsMessage_AssistantResponds()
    {
        // User sends message
        var correlationId = _pipeline.SubmitUserMessage("topic-1", "Hello", "user-1");

        // Streaming starts
        _dispatcher.Dispatch(new StreamStarted("topic-1"));

        // Chunks arrive
        _pipeline.AccumulateChunk("topic-1", "msg-1", "Hi ", null, null);
        _pipeline.AccumulateChunk("topic-1", "msg-1", "there!", null, null);

        // Stream completes
        _pipeline.FinalizeMessage("topic-1", "msg-1");

        // Verify final state
        var messages = _messagesStore.State.MessagesByTopic["topic-1"];
        messages.Count.ShouldBe(2);
        messages[0].Role.ShouldBe("user");
        messages[0].Content.ShouldBe("Hello");
        messages[1].Role.ShouldBe("assistant");
        messages[1].Content.ShouldBe("Hi there!");
    }

    [Fact]
    public void DuplicateFinalization_SkipsSecondAttempt()
    {
        // Start streaming
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _pipeline.AccumulateChunk("topic-1", "msg-1", "Response", null, null);

        // First finalize
        _pipeline.FinalizeMessage("topic-1", "msg-1");

        // Simulate race condition - another source tries to finalize same message
        _pipeline.AccumulateChunk("topic-1", "msg-1", " extra", null, null);
        _pipeline.FinalizeMessage("topic-1", "msg-1");

        // Should only have one message
        var messages = _messagesStore.State.MessagesByTopic["topic-1"];
        messages.Count.ShouldBe(1);
        messages[0].Content.ShouldBe("Response");
    }

    [Fact]
    public void LoadHistory_ThenStream_NoDoubleMessages()
    {
        // Load history with existing message
        var history = new List<ChatHistoryMessage>
        {
            new() { MessageId = "msg-1", Role = "assistant", Content = "Previous response" }
        };
        _pipeline.LoadHistory("topic-1", history);

        // Start new stream
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _pipeline.AccumulateChunk("topic-1", "msg-2", "New response", null, null);
        _pipeline.FinalizeMessage("topic-1", "msg-2");

        // Should have both messages
        var messages = _messagesStore.State.MessagesByTopic["topic-1"];
        messages.Count.ShouldBe(2);
    }

    [Fact]
    public void OtherUserMessage_SkippedWhenSentByThisClient()
    {
        // User sends message through pipeline
        var correlationId = _pipeline.SubmitUserMessage("topic-1", "Hello", "user-1");

        // Simulate hub notification with same correlation ID
        var wasSent = _pipeline.WasSentByThisClient(correlationId);

        wasSent.ShouldBeTrue();
    }
}
```

**Step 2: Run tests**

Run: `dotnet test Tests --filter "FullyQualifiedName~MessagePipelineIntegrationTests" -v n`
Expected: PASS

**Step 3: Commit**

```bash
git add Tests/Unit/WebChat.Client/State/Pipeline/
git commit -m "test(webchat): add integration tests for MessagePipeline"
```

---

## Task 13: Run Full Test Suite and Verify

**Step 1: Run all unit tests**

Run: `dotnet test Tests/Unit -v n`
Expected: All PASS

**Step 2: Build entire solution**

Run: `dotnet build`
Expected: SUCCESS with no warnings related to pipeline

**Step 3: Commit summary**

```bash
git log --oneline -15
```

Review commits to ensure clean history.

---

## Summary

After completing all tasks, you will have:

1. **New `IMessagePipeline` interface** - Single entry point for all message operations
2. **`MessagePipeline` implementation** - Handles deduplication, accumulation tracking, and logging
3. **Migrated sources**:
   - `SendMessageEffect` - Uses `pipeline.SubmitUserMessage()`
   - `HubEventDispatcher` - Uses `pipeline.WasSentByThisClient()` and `pipeline.FinalizeMessage()`
   - `TopicSelectionEffect` - Uses `pipeline.LoadHistory()`
   - `InitializationEffect` - Uses `pipeline.LoadHistory()`
   - `StreamResumeService` - Uses `pipeline.ResumeFromBuffer()`
4. **Deleted `SentMessageTracker`** - Functionality absorbed by pipeline
5. **Comprehensive tests** - Unit tests for pipeline, integration tests for full flow

**Phase 2 complete.** The pipeline is now the single coordination point. Future work (Phase 3-4) can:
- Move streaming accumulation fully into pipeline
- Simplify store reducers to pure setters
- Remove remaining dedup logic from `MessagesReducers`

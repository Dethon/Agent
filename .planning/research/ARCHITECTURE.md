# Architecture Patterns: Unidirectional Data Flow for Blazor WebAssembly + SignalR

**Domain:** Real-time chat client with SignalR streaming
**Researched:** 2026-01-19
**Confidence:** HIGH (verified patterns from multiple authoritative sources)

## Executive Summary

This document outlines the recommended architecture for refactoring the WebChat.Client to use unidirectional data flow while maintaining Clean Architecture principles. The current implementation mixes concerns: `StreamingCoordinator` handles state mutation, stream processing, and render throttling (416 lines). The refactored architecture separates these into distinct components with clear boundaries.

## Current Architecture Analysis

### Current Component Map

```
ChatContainer.razor (320 lines)
    |
    +-- IChatConnectionService (connection lifecycle)
    +-- IChatSessionService (session management)
    +-- IChatStateManager (state container - 272 lines, mutable)
    +-- IAgentService (agent list)
    +-- ITopicService (topic CRUD)
    +-- IChatMessagingService (SignalR hub calls)
    +-- IStreamingCoordinator (stream processing + state mutation)
    +-- StreamResumeService (resume logic + state mutation)
    +-- SignalREventSubscriber (event subscription)
```

### Current Problems

1. **State Mutation from Multiple Sources**: `StreamingCoordinator`, `StreamResumeService`, and `ChatNotificationHandler` all mutate `ChatStateManager` directly
2. **Bidirectional Data Flow**: Components both read state and mutate it through various services
3. **Coupled Concerns**: `StreamingCoordinator` handles stream processing, state accumulation, throttling, and buffer reconstruction
4. **Render Callback Threading**: `Func<Task> onRender` passed through multiple layers creates coupling
5. **Missing Clean Architecture Alignment**: `INotifier` is in Domain but implemented in Agent layer (should be Infrastructure)

## Recommended Architecture: Flux-Inspired Unidirectional Flow

### Core Pattern

```
+-----------+     +------------+     +---------+     +-------+
|   View    | --> | Dispatcher | --> | Reducer | --> | Store |
| (Blazor)  |     |  (Actions) |     | (State) |     | (Immutable)
+-----------+     +------------+     +---------+     +-------+
      ^                                                   |
      |                                                   |
      +---------------------------------------------------+
                    (Subscribe to state changes)
```

### Component Boundaries

| Component | Responsibility | Interacts With |
|-----------|---------------|----------------|
| **Store (IChatStore)** | Holds immutable state, notifies subscribers | Reducers only write; Views only read |
| **Actions** | Immutable records describing intent | Created by Views and Effects |
| **Reducers** | Pure functions: (State, Action) -> State | Called by Dispatcher |
| **Effects** | Side effects (SignalR calls, persistence) | Dispatch actions after completion |
| **Views (Components)** | Render state, dispatch actions | Never mutate state directly |

### SignalR Integration Pattern

SignalR events flow through the same unidirectional pattern:

```
SignalR Hub
    |
    v
SignalREventHandler (Effect)
    |
    v
Dispatch Action (e.g., StreamChunkReceived)
    |
    v
Reducer (updates state immutably)
    |
    v
Store notifies subscribers
    |
    v
Components re-render
```

## Proposed Component Design

### 1. State Store (Single Source of Truth)

```
WebChat.Client/
  State/
    ChatState.cs              # Immutable state record
    IChatStore.cs             # Store interface
    ChatStore.cs              # Implementation with INotifyPropertyChanged
```

**ChatState** (immutable record):
```csharp
public sealed record ChatState
{
    public ImmutableList<AgentInfo> Agents { get; init; }
    public string? SelectedAgentId { get; init; }
    public ImmutableList<StoredTopic> Topics { get; init; }
    public string? SelectedTopicId { get; init; }
    public ImmutableDictionary<string, ImmutableList<ChatMessageModel>> MessagesByTopic { get; init; }
    public ImmutableDictionary<string, ChatMessageModel?> StreamingMessageByTopic { get; init; }
    public ImmutableHashSet<string> StreamingTopics { get; init; }
    public ToolApprovalRequestMessage? CurrentApprovalRequest { get; init; }
    public ConnectionState ConnectionState { get; init; }
}
```

**IChatStore**:
```csharp
public interface IChatStore
{
    ChatState State { get; }
    event Action? OnStateChanged;
    void Dispatch(IChatAction action);
}
```

### 2. Actions (Intent Description)

```
WebChat.Client/
  State/
    Actions/
      IChatAction.cs                  # Marker interface
      AgentActions.cs                 # SetAgents, SelectAgent
      TopicActions.cs                 # SelectTopic, AddTopic, RemoveTopic, UpdateTopic
      MessageActions.cs               # AddMessage, SetMessages, UpdateStreamingMessage
      StreamActions.cs                # StartStreaming, StopStreaming, StreamChunkReceived
      ConnectionActions.cs            # Connected, Disconnected, Reconnecting
      ApprovalActions.cs              # SetApprovalRequest, ClearApprovalRequest
```

Example action records:
```csharp
public sealed record StreamChunkReceived(
    string TopicId,
    string? Content,
    string? Reasoning,
    string? ToolCalls,
    string? MessageId,
    bool IsComplete) : IChatAction;

public sealed record SelectTopic(string TopicId) : IChatAction;
```

### 3. Reducers (Pure State Transitions)

```
WebChat.Client/
  State/
    Reducers/
      IChatReducer.cs
      AgentReducer.cs
      TopicReducer.cs
      MessageReducer.cs
      StreamReducer.cs
      ConnectionReducer.cs
      ApprovalReducer.cs
```

**Reducer pattern** (pure function):
```csharp
public sealed class StreamReducer : IChatReducer
{
    public ChatState Reduce(ChatState state, IChatAction action) => action switch
    {
        StreamChunkReceived chunk => ReduceStreamChunk(state, chunk),
        StartStreaming start => state with { StreamingTopics = state.StreamingTopics.Add(start.TopicId) },
        StopStreaming stop => state with { StreamingTopics = state.StreamingTopics.Remove(stop.TopicId) },
        _ => state
    };

    private static ChatState ReduceStreamChunk(ChatState state, StreamChunkReceived chunk)
    {
        var current = state.StreamingMessageByTopic.GetValueOrDefault(chunk.TopicId)
                      ?? new ChatMessageModel { Role = "assistant" };

        var updated = current with
        {
            Content = AccumulateContent(current.Content, chunk.Content),
            Reasoning = AccumulateContent(current.Reasoning, chunk.Reasoning),
            ToolCalls = AccumulateContent(current.ToolCalls, chunk.ToolCalls)
        };

        return state with
        {
            StreamingMessageByTopic = state.StreamingMessageByTopic.SetItem(chunk.TopicId, updated)
        };
    }

    private static string AccumulateContent(string? existing, string? addition)
        => string.IsNullOrEmpty(addition) ? existing ?? "" : (existing ?? "") + addition;
}
```

### 4. Effects (Side Effects and Async Operations)

```
WebChat.Client/
  State/
    Effects/
      IEffect.cs
      SignalRStreamEffect.cs          # Handles streaming from SignalR
      TopicPersistenceEffect.cs       # Saves topics to server
      HistoryLoadEffect.cs            # Loads message history
      SessionEffect.cs                # Manages SignalR sessions
```

**Effect pattern** (handles side effects, dispatches results):
```csharp
public sealed class SignalRStreamEffect(
    IChatMessagingService messagingService,
    IChatStore store)
{
    public async Task StreamResponseAsync(string topicId, string message)
    {
        store.Dispatch(new StartStreaming(topicId));

        try
        {
            await foreach (var chunk in messagingService.SendMessageAsync(topicId, message))
            {
                if (chunk.ApprovalRequest is not null)
                {
                    store.Dispatch(new SetApprovalRequest(chunk.ApprovalRequest));
                    continue;
                }

                if (chunk.Error is not null)
                {
                    store.Dispatch(new StreamError(topicId, chunk.Error));
                    break;
                }

                store.Dispatch(new StreamChunkReceived(
                    topicId,
                    chunk.Content,
                    chunk.Reasoning,
                    chunk.ToolCalls,
                    chunk.MessageId,
                    chunk.IsComplete));
            }

            store.Dispatch(new StreamCompleted(topicId));
        }
        finally
        {
            store.Dispatch(new StopStreaming(topicId));
        }
    }
}
```

### 5. SignalR Event Handler (Server-Push to Actions)

```
WebChat.Client/
  State/
    Handlers/
      SignalREventHandler.cs
```

```csharp
public sealed class SignalREventHandler(IChatStore store)
{
    public void HandleTopicChanged(TopicChangedNotification notification)
    {
        var action = notification.ChangeType switch
        {
            TopicChangeType.Created => new AddTopic(StoredTopic.FromMetadata(notification.Topic!)),
            TopicChangeType.Updated => new UpdateTopic(notification.Topic!),
            TopicChangeType.Deleted => new RemoveTopic(notification.TopicId),
            _ => (IChatAction?)null
        };

        if (action is not null)
        {
            store.Dispatch(action);
        }
    }

    public void HandleStreamChanged(StreamChangedNotification notification)
    {
        var action = notification.ChangeType switch
        {
            StreamChangeType.Started => new ExternalStreamStarted(notification.TopicId),
            StreamChangeType.Cancelled => new StopStreaming(notification.TopicId),
            StreamChangeType.Completed => new StreamCompleted(notification.TopicId),
            _ => (IChatAction?)null
        };

        if (action is not null)
        {
            store.Dispatch(action);
        }
    }
}
```

### 6. View Components (Render and Dispatch)

Components become pure renderers that:
1. Subscribe to store state changes
2. Render based on current state
3. Dispatch actions in response to user events

```razor
@inherits StoreSubscriberComponent

<div class="chat-container">
    <MessageList Messages="@CurrentMessages"
                 StreamingMessage="@CurrentStreamingMessage" />

    <ChatInput OnSend="@HandleSend"
               Disabled="@(!CanSend)" />
</div>

@code {
    private IReadOnlyList<ChatMessageModel> CurrentMessages =>
        Store.State.SelectedTopicId is { } topicId
            ? Store.State.MessagesByTopic.GetValueOrDefault(topicId, [])
            : [];

    private ChatMessageModel? CurrentStreamingMessage =>
        Store.State.SelectedTopicId is { } topicId
            ? Store.State.StreamingMessageByTopic.GetValueOrDefault(topicId)
            : null;

    private bool CanSend =>
        Store.State.SelectedAgentId is not null &&
        Store.State.ConnectionState == ConnectionState.Connected;

    private void HandleSend(string message)
    {
        if (Store.State.SelectedTopicId is null)
        {
            // Effect handles topic creation and session start
            _ = Effects.CreateTopicAndSendAsync(message);
        }
        else
        {
            Store.Dispatch(new AddMessage(Store.State.SelectedTopicId, new ChatMessageModel
            {
                Role = "user",
                Content = message
            }));
            _ = Effects.SendMessageAsync(Store.State.SelectedTopicId, message);
        }
    }
}
```

### 7. Base Subscriber Component

```csharp
public abstract class StoreSubscriberComponent : ComponentBase, IDisposable
{
    [Inject] protected IChatStore Store { get; set; } = null!;
    [Inject] protected ChatEffects Effects { get; set; } = null!;

    protected override void OnInitialized()
    {
        Store.OnStateChanged += HandleStateChanged;
    }

    private void HandleStateChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        Store.OnStateChanged -= HandleStateChanged;
    }
}
```

## Data Flow Diagrams

### User Sends Message Flow

```
User clicks Send
       |
       v
ChatInput.HandleSend()
       |
       v
Store.Dispatch(AddMessage)  <-- Optimistic update
       |
       v
Reducer updates state (message added to list)
       |
       v
Store notifies subscribers
       |
       v
MessageList re-renders with new message
       |
       | (parallel)
       v
SignalRStreamEffect.StreamResponseAsync()
       |
       v
SignalR Hub call: SendMessage
       |
       v
foreach chunk in stream:
    Store.Dispatch(StreamChunkReceived)
       |
       v
    Reducer accumulates chunk
       |
       v
    Store notifies subscribers
       |
       v
    MessageList re-renders streaming message
```

### SignalR Server Push Flow

```
Server pushes notification
       |
       v
HubConnection.On<T> handler
       |
       v
SignalREventHandler.HandleXxx()
       |
       v
Store.Dispatch(action)
       |
       v
Reducer updates state
       |
       v
Store notifies subscribers
       |
       v
Components re-render
```

### Reconnection Flow

```
Connection drops
       |
       v
ConnectionService.OnReconnecting
       |
       v
Store.Dispatch(ConnectionStateChanged(Reconnecting))
       |
       v
UI shows reconnecting indicator
       |
       v
Connection restored
       |
       v
ConnectionService.OnReconnected
       |
       v
Store.Dispatch(ConnectionStateChanged(Connected))
       |
       v
StreamResumeEffect: foreach topic where IsProcessing
    |
    v
    Store.Dispatch(StreamChunkReceived) for buffered messages
    |
    v
    Resume live stream
```

## Clean Architecture Alignment

### Layer Mapping

| Blazor Client Layer | Clean Architecture Layer | Contents |
|---------------------|-------------------------|----------|
| State/Actions | Application/Domain | Intent records (pure data) |
| State/Reducers | Application | State transition logic |
| State/Store | Application | State container |
| State/Effects | Infrastructure | SignalR calls, persistence |
| State/Handlers | Infrastructure | SignalR event handling |
| Components | Presentation | Razor views |
| Services | Infrastructure | SignalR connection, HTTP |
| Models | Domain | Entity models |
| Contracts | Domain | Interfaces |

### INotifier Refactoring

**Current problem**: `INotifier` is defined in Domain but requires SignalR (Infrastructure concern).

**Solution**: Move the interface to align with its actual abstraction level:

```
Domain/Contracts/
  INotifier.cs  <-- REMOVE (SignalR-specific)

Infrastructure/Contracts/
  INotifier.cs  <-- ADD (Infrastructure-level abstraction)

Agent/Hubs/
  Notifier.cs   <-- Implementation stays here (uses IHubContext)
```

The interface is an Infrastructure concern because:
1. It's specifically for push notifications (not domain logic)
2. The only implementation uses SignalR
3. Domain layer shouldn't know about notification mechanisms

### Dependency Flow

```
WebChat.Client (Presentation)
       |
       v
State/* (Application) - Actions, Reducers, Store
       |
       v
Services/* (Infrastructure) - SignalR, HTTP
       |
       v
Models/Contracts (Domain) - Shared types
```

## Refactoring Order (Suggested Phases)

### Phase 1: State Foundation
1. Create immutable `ChatState` record
2. Create `IChatStore` interface and basic implementation
3. Define core actions (SelectTopic, AddMessage, etc.)
4. Implement reducers for basic actions
5. **Test**: Unit test reducers are pure functions

### Phase 2: Migrate State Management
1. Replace `ChatStateManager` usage in components with `IChatStore`
2. Create `StoreSubscriberComponent` base class
3. Update `ChatContainer` to dispatch actions instead of calling services
4. **Test**: Verify components render correctly from store state

### Phase 3: Extract Effects
1. Move SignalR streaming logic from `StreamingCoordinator` to `SignalRStreamEffect`
2. Move stream resume logic from `StreamResumeService` to effect
3. Create `TopicPersistenceEffect` for topic save/delete operations
4. **Test**: Integration test effects dispatch correct actions

### Phase 4: SignalR Event Handling
1. Replace `ChatNotificationHandler` with `SignalREventHandler`
2. Have handler dispatch actions instead of mutating state
3. Simplify `SignalREventSubscriber` to just wire events to handler
4. **Test**: Verify server push events update state correctly

### Phase 5: Remove Legacy
1. Delete `ChatStateManager` (replaced by `ChatStore`)
2. Delete `StreamingCoordinator` (replaced by reducers + effects)
3. Delete `StreamResumeService` (replaced by effect)
4. Simplify component code
5. **Test**: Full integration test of chat flow

### Phase 6: Clean Architecture Alignment
1. Move `INotifier` from Domain to Infrastructure
2. Update DI registrations
3. Document final architecture

## Render Throttling Strategy

**Current approach**: `StreamingCoordinator.ThrottledRenderAsync` with manual timing.

**Recommended approach**: Move throttling to the store or use Blazor's built-in batching.

Option A: Store-level batching
```csharp
public sealed class ChatStore : IChatStore
{
    private readonly Timer _batchTimer;
    private bool _pendingNotification;

    private void NotifySubscribers()
    {
        if (!_pendingNotification)
        {
            _pendingNotification = true;
            _batchTimer.Change(50, Timeout.Infinite); // 50ms batch window
        }
    }

    private void OnBatchTimerElapsed(object? _)
    {
        _pendingNotification = false;
        OnStateChanged?.Invoke();
    }
}
```

Option B: Component-level throttling (simpler)
```csharp
public abstract class ThrottledStoreSubscriberComponent : StoreSubscriberComponent
{
    private DateTime _lastRender = DateTime.MinValue;
    private const int ThrottleMs = 50;

    private async void HandleStateChanged()
    {
        var elapsed = (DateTime.UtcNow - _lastRender).TotalMilliseconds;
        if (elapsed >= ThrottleMs)
        {
            _lastRender = DateTime.UtcNow;
            await InvokeAsync(StateHasChanged);
        }
    }
}
```

## Library Consideration: Fluxor

Fluxor is the most popular Flux library for Blazor. Consider using it if:
- You want battle-tested infrastructure
- You need middleware support (logging, devtools)
- Team is familiar with Redux patterns

**Pros:**
- Zero boilerplate with source generators
- Built-in effects and middleware
- Active community

**Cons:**
- Additional dependency
- Learning curve for team
- May be overkill for focused chat application

**Recommendation**: Implement custom minimal Flux pattern first. The codebase is focused enough that a full Fluxor adoption adds unnecessary complexity. The patterns above give the benefits of unidirectional flow without the library overhead.

## Sources

- [Using Fluxor for State Management in Blazor - Code Maze](https://code-maze.com/fluxor-for-state-management-in-blazor/)
- [Understanding Flux Patterns in Blazor - Toxigon](https://toxigon.com/flux-patterns-in-blazor)
- [Advanced Blazor State Management Using Fluxor, part 7 - SignalR - DEV Community](https://dev.to/mr_eking/advanced-blazor-state-management-using-fluxor-part-7-client-to-client-comms-with-signalr-4p9)
- [Blazor Basics: Real-time Web Apps with WebAssembly & SignalR - Telerik](https://www.telerik.com/blogs/blazor-basics-real-time-web-applications-blazor-webassembly-signalr)
- [AppState Pattern in .NET and Blazor - Medium](https://medium.com/@robhutton8/appstate-pattern-in-net-and-blazor-efficient-state-management-for-modern-applications-f3150d35b79a)
- [Building Blazor WebAssembly Apps with Clean Architecture - EzzyLearning](https://www.ezzylearning.net/tutorial/building-blazor-webassembly-apps-with-clean-architecture)
- [Blazor State Management: Best Practices - Infragistics](https://www.infragistics.com/blogs/blazor-state-management/)
- [Microsoft Learn: SignalR with Blazor](https://learn.microsoft.com/en-us/aspnet/core/blazor/tutorials/signalr-blazor)

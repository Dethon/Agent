---
phase: 05
plan: 06
subsystem: webchat-components
tags: [blazor, stores, subscription, action-dispatch, message-list]
dependency-graph:
  requires: [05-01]
  provides: [message-list-store-subscription]
  affects: [05-02, 05-03]
tech-stack:
  added: []
  patterns: [multi-store-subscription, topic-based-filtering]
key-files:
  created: []
  modified:
    - WebChat.Client/Components/Chat/MessageList.razor
decisions:
  - id: call-update-on-topic-change
    choice: "UpdateStreamingStatus() called when topic changes"
    reason: "Streaming status depends on current topic ID; must re-evaluate when topic changes"
metrics:
  duration: ~2 minutes
  completed: 2026-01-20
---

# Phase 5 Plan 6: MessageList Store Migration Summary

MessageList component migrated from parameter-based props to multi-store subscriptions with action dispatch for suggestions.

## What Was Built

### MessageList Component Migration

Converted from parameter drilling to direct store subscriptions:

**Before:**
```razor
@inject IJSRuntime Js

[Parameter] public IReadOnlyList<ChatMessageModel> Messages { get; set; } = [];
[Parameter] public string TopicId { get; set; } = "";
[Parameter] public bool IsStreaming { get; set; }
[Parameter] public AgentInfo? SelectedAgent { get; set; }
[Parameter] public EventCallback<string> OnSuggestionClicked { get; set; }
```

**After:**
```razor
@using WebChat.Client.State
@using WebChat.Client.State.Messages
@using WebChat.Client.State.Topics
@using WebChat.Client.State.Streaming
@inherits StoreSubscriberComponent

@inject MessagesStore MessagesStore
@inject TopicsStore TopicsStore
@inject StreamingStore StreamingStore
@inject IDispatcher Dispatcher
@inject IJSRuntime Js

private IReadOnlyList<ChatMessageModel> _messages = [];
private string? _topicId;
private bool _isStreaming;
private AgentInfo? _selectedAgent;
```

### Multi-Store Subscriptions

Four subscriptions established in OnInitialized:

1. **Topic ID subscription** - Updates local topic and triggers message/streaming refresh
2. **Agent subscription** - Gets selected agent from TopicsStore for EmptyState display
3. **Messages subscription** - Re-fetches messages when MessagesStore changes
4. **Streaming subscription** - Updates streaming status when StreamingTopics changes

```csharp
protected override void OnInitialized()
{
    Subscribe(TopicsStore.StateObservable,
        state => state.SelectedTopicId,
        id => {
            _topicId = id;
            UpdateMessages();
            UpdateStreamingStatus();
        });

    Subscribe(TopicsStore.StateObservable,
        state => state.Agents.FirstOrDefault(a => a.Id == state.SelectedAgentId),
        agent => _selectedAgent = agent);

    Subscribe(MessagesStore.StateObservable,
        state => state,
        _ => UpdateMessages());

    Subscribe(StreamingStore.StateObservable,
        state => state.StreamingTopics,
        _ => UpdateStreamingStatus());
}
```

### Action Dispatch for Suggestions

Suggestion clicks now dispatch SendMessage action instead of invoking EventCallback:

```csharp
private void HandleSuggestionClicked(string suggestion)
{
    Dispatcher.Dispatch(new SendMessage(_topicId, suggestion));
}
```

### Update Methods

Two helper methods for topic-dependent state updates:

```csharp
private void UpdateMessages()
{
    _messages = _topicId != null
        ? MessagesStore.State.MessagesByTopic.GetValueOrDefault(_topicId, [])
        : [];
}

private void UpdateStreamingStatus()
{
    _isStreaming = _topicId != null && StreamingStore.State.StreamingTopics.Contains(_topicId);
}
```

## Patterns Established

### Multi-Store Subscription Pattern

When a component needs data from multiple stores:
```razor
@inject StoreA StoreA
@inject StoreB StoreB

protected override void OnInitialized()
{
    Subscribe(StoreA.StateObservable, ...);
    Subscribe(StoreB.StateObservable, ...);
}
```

### Topic-Based Data Access Pattern

For topic-scoped data, store topic ID locally and use it to query store state:
```csharp
_topicId = id;
_messages = MessagesStore.State.MessagesByTopic.GetValueOrDefault(_topicId, []);
```

### Dependent State Update Pattern

When state depends on multiple sources, update derived state when any source changes:
```csharp
Subscribe(TopicsStore.StateObservable,
    state => state.SelectedTopicId,
    id => {
        _topicId = id;
        UpdateMessages();       // Depends on _topicId
        UpdateStreamingStatus(); // Depends on _topicId
    });
```

## Decisions Made

| Decision | Rationale |
|----------|-----------|
| Call UpdateStreamingStatus on topic change | Streaming status depends on current topic; must re-evaluate when selection changes |
| Subscribe to full MessagesState | MessagesByTopic is dictionary; DistinctUntilChanged on selector would require custom comparer |
| EmptyState keeps SelectedAgent parameter | Simple display component; no benefit from store subscription |

## Deviations from Plan

**[Rule 3 - Blocking] Fixed property name mismatch**

- **Found during:** Task 1 implementation
- **Issue:** Plan referenced `ActiveStreams` but actual property is `StreamingTopics`
- **Fix:** Used `StreamingTopics` property instead
- **Files modified:** MessageList.razor

Note: The implementation for this plan was completed in commit `5cc097a` (fix(05-03): fix blocking build errors from previous plan) which migrated MessageList alongside other fixes. This summary documents the work as specified in plan 05-06.

## Verification Results

All criteria verified:
- [x] Build succeeds: `dotnet build WebChat.Client`
- [x] MessageList inherits StoreSubscriberComponent
- [x] Subscribes to MessagesStore, TopicsStore, StreamingStore
- [x] Dispatches SendMessage for suggestion clicks
- [x] No [Parameter] attributes
- [x] Code block under 80 lines (77 lines)

## Next Phase Readiness

**Ready for:** Remaining Phase 5 plans and container component migration

**Dependencies met:**
- MessageList subscribes to stores directly
- No parameter drilling from ChatContainer
- Action dispatch pattern for user interactions

**Blockers:** None

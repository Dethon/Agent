---
phase: 05
plan: 04
subsystem: webchat-effects
tags: [blazor, effects, async-coordination, fire-and-forget]
dependency-graph:
  requires: [05-01, 05-02]
  provides: [effect-pattern, send-message-coordination, topic-selection-coordination, topic-delete-coordination]
  affects: [05-05, 05-06]
tech-stack:
  added: []
  patterns: [effect-handler-pattern, fire-and-forget-async, dispatcher-handler-registration]
key-files:
  created:
    - WebChat.Client/State/Effects/SendMessageEffect.cs
    - WebChat.Client/State/Effects/TopicSelectionEffect.cs
    - WebChat.Client/State/Effects/TopicDeleteEffect.cs
  modified:
    - WebChat.Client/Program.cs
decisions:
  - id: fire-and-forget-pattern
    choice: "Use _ = HandleAsync(action) in sync handlers"
    reason: "Effects register sync handlers with dispatcher, async operations run fire-and-forget"
  - id: noop-render-callback
    choice: "Pass () => Task.CompletedTask to StreamingCoordinator"
    reason: "Store-based components subscribe directly, render callbacks unnecessary"
  - id: messages-loaded-reuse
    choice: "Use MessagesLoaded instead of new SetMessages action"
    reason: "Existing action already provides same functionality, avoid duplication"
metrics:
  duration: ~3 minutes
  completed: 2026-01-20
---

# Phase 5 Plan 4: Effects Summary

Effect classes created for complex async operations: SendMessage, TopicSelection, and TopicDelete, separating business logic from render logic.

## What Was Built

### SendMessageEffect

Coordinates new message sending with topic creation if needed:

```csharp
public sealed class SendMessageEffect : IDisposable
{
    public SendMessageEffect(Dispatcher dispatcher, ...)
    {
        dispatcher.RegisterHandler<SendMessage>(HandleSendMessage);
    }

    private void HandleSendMessage(SendMessage action)
    {
        _ = HandleSendMessageAsync(action);
    }

    private async Task HandleSendMessageAsync(SendMessage action)
    {
        // Create topic if TopicId is null
        // Start session
        // Add user message to store
        // Start streaming
        // Fire-and-forget StreamResponseAsync
    }
}
```

Key features:
- Creates new topic if `TopicId` is null (new conversation)
- Starts session with server
- Dispatches AddMessage and StreamStarted actions
- Fires off streaming coordinator (fire-and-forget)

### TopicSelectionEffect

Handles topic selection with history loading and stream resume:

```csharp
public sealed class TopicSelectionEffect : IDisposable
{
    public TopicSelectionEffect(Dispatcher dispatcher, ...)
    {
        dispatcher.RegisterHandler<SelectTopic>(HandleSelectTopic);
    }

    private async Task HandleSelectTopicAsync(string topicId)
    {
        // Check if messages already loaded
        // If not, load from server
        // Dispatch MessagesLoaded
        // Try to resume any active streaming
    }
}
```

Key features:
- Loads message history only if not cached
- Starts session for selected topic
- Resumes active streaming via StreamResumeService

### TopicDeleteEffect

Handles topic deletion with cleanup:

```csharp
public sealed class TopicDeleteEffect : IDisposable
{
    public TopicDeleteEffect(Dispatcher dispatcher, ...)
    {
        dispatcher.RegisterHandler<RemoveTopic>(HandleRemoveTopic);
    }

    private async Task HandleRemoveTopicAsync(string topicId)
    {
        // Cancel active streaming
        // Delete from server
        // Clear approval if selected topic
    }
}
```

Key features:
- Cancels any active streaming for the topic
- Deletes topic from server
- Clears approval modal if deleting selected topic

### DI Registration

All effects registered in Program.cs:

```csharp
// State effects (Phase 4 & 5)
builder.Services.AddScoped<ReconnectionEffect>();
builder.Services.AddScoped<SendMessageEffect>();
builder.Services.AddScoped<TopicSelectionEffect>();
builder.Services.AddScoped<TopicDeleteEffect>();

// Eagerly instantiate at startup
_ = app.Services.GetRequiredService<ReconnectionEffect>();
_ = app.Services.GetRequiredService<SendMessageEffect>();
_ = app.Services.GetRequiredService<TopicSelectionEffect>();
_ = app.Services.GetRequiredService<TopicDeleteEffect>();
```

## Patterns Established

### Effect Handler Pattern

Effects register sync handlers with the dispatcher but run async operations fire-and-forget:

```csharp
dispatcher.RegisterHandler<SomeAction>(action => {
    _ = HandleAsync(action);
});
```

This pattern keeps the dispatcher's synchronous contract while allowing effects to perform async work.

### No-op Render Callback

Since components now subscribe to stores directly, the `onRender` callback passed to `StreamingCoordinator` can be a no-op:

```csharp
_ = _streamingCoordinator.StreamResponseAsync(topic, message, () => Task.CompletedTask);
```

## Task Commits

1. **Task 1: Create SendMessageEffect** - `4a94b4b` (feat)
2. **Task 2: Create TopicSelectionEffect** - `7bf4e47` (feat)
3. **Task 3: Create TopicDeleteEffect and DI registration** - `602c98a` (feat)

## Decisions Made

| Decision | Rationale |
|----------|-----------|
| Fire-and-forget async | Effects register sync handlers; async work runs without blocking dispatch |
| No-op render callback | Store subscriptions handle re-renders; callback is legacy bridge |
| Reuse MessagesLoaded | Existing action sufficient; no need for redundant SetMessages |

## Deviations from Plan

None - plan executed exactly as written.

## Next Phase Readiness

**Ready for:** Plan 05 - Container component migration (ChatArea)

**Dependencies met:**
- All three core effects implemented
- Effects registered and activated at startup
- Fire-and-forget pattern established for async operations

**Blockers:** None

---
phase: 04-signalr-integration
plan: 03
subsystem: state-hub-effects
tags: [SignalR, reconnection, effects, stores, Blazor, reactive]

dependency-graph:
  requires: [04-01, 04-02]
  provides: [ReconnectionEffect, store-based-resumption]
  affects: [05-01, 05-02]

tech-stack:
  added: []
  patterns: [effect-pattern, store-subscription, reactive-state-transitions]

key-files:
  created:
    - WebChat.Client/State/Hub/ReconnectionEffect.cs
    - Tests/Unit/WebChat.Client/State/ReconnectionEffectTests.cs
  modified:
    - WebChat.Client/Services/Streaming/StreamResumeService.cs
    - WebChat.Client/Components/Chat/ChatContainer.razor
    - WebChat.Client/Program.cs
    - Tests/Integration/WebChat/Client/StreamResumeServiceIntegrationTests.cs
    - Tests/Integration/WebChat/Client/NotificationHandlerIntegrationTests.cs

decisions:
  - id: effect-pattern
    choice: "ReconnectionEffect subscribes to ConnectionStore and reacts to state transitions"
    rationale: "Centralizes reconnection logic; components no longer need to handle reconnection events directly"

  - id: state-transition-detection
    choice: "Track previous status and detect Reconnecting->Connected transition"
    rationale: "BehaviorSubject emits current value on subscribe; need to track previous state to detect actual reconnections vs fresh connections"

  - id: fire-and-forget-resumption
    choice: "Use fire-and-forget for session restart and stream resumption"
    rationale: "Reconnection effect runs synchronously in response to state change; async operations should not block the subscription"

metrics:
  duration: ~5 minutes
  completed: 2026-01-20
---

# Phase 04 Plan 03: Stream Resumption Integration Summary

ReconnectionEffect centralizes reconnection handling via store-based state transitions, replacing component-level event handlers.

## What Was Built

### ReconnectionEffect (New)

Effect that subscribes to ConnectionStore and triggers reconnection actions:

```csharp
public sealed class ReconnectionEffect : IDisposable
{
    private readonly IDisposable _subscription;
    private ConnectionStatus _previousStatus = ConnectionStatus.Disconnected;

    public ReconnectionEffect(
        ConnectionStore connectionStore,
        TopicsStore topicsStore,
        IChatSessionService sessionService,
        IStreamResumeService streamResumeService)
    {
        _subscription = connectionStore.StateObservable
            .Subscribe(state =>
            {
                var wasReconnecting = _previousStatus == ConnectionStatus.Reconnecting;
                var isNowConnected = state.Status == ConnectionStatus.Connected;
                _previousStatus = state.Status;

                if (wasReconnecting && isNowConnected)
                {
                    HandleReconnected(topicsStore, sessionService, streamResumeService);
                }
            });
    }
}
```

Key behaviors:
- Tracks previous connection status to detect actual reconnections
- Restarts session for currently selected topic
- Resumes streams for all topics (fire-and-forget)
- Activated at startup via explicit service resolution

### StreamResumeService Updates

Replaced direct state mutations with dispatcher actions:

| Old (stateManager)           | New (dispatcher)                    |
|------------------------------|-------------------------------------|
| TryStartResuming             | StartResuming action                |
| IsTopicStreaming             | StreamingStore.State check          |
| SetMessagesForTopic          | MessagesLoaded action               |
| StartStreaming               | StreamStarted action                |
| UpdateStreamingMessage       | StreamChunk action                  |
| SetApprovalRequest           | ShowApproval action                 |
| StopResuming                 | StopResuming action                 |

Kept stateManager for HasMessagesForTopic/GetMessagesForTopic (Phase 5 migration).

### ChatContainer Cleanup

Removed:
- `HandleReconnected` method
- `ConnectionService.OnReconnected += HandleReconnected` subscription
- `ConnectionService.OnReconnected -= HandleReconnected` unsubscription

Reconnection now handled entirely by ReconnectionEffect.

## Testing

5 unit tests verify ReconnectionEffect behavior:

1. `WhenConnectionReconnected_StartsSessionForSelectedTopic` - session restart
2. `WhenConnectionReconnected_ResumesStreamsForAllTopics` - stream resumption
3. `WhenConnectionConnectedWithoutPriorReconnecting_DoesNotTriggerReconnection` - fresh connection
4. `WhenConnectionReconnecting_DoesNotTriggerYet` - still reconnecting
5. `Dispose_UnsubscribesFromStore` - cleanup verification

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Integration tests needed constructor updates**
- **Found during:** Task 3
- **Issue:** StreamResumeServiceIntegrationTests and NotificationHandlerIntegrationTests created StreamResumeService with old constructor signature
- **Fix:** Added IDispatcher and StreamingStore parameters to all test instances
- **Files modified:** StreamResumeServiceIntegrationTests.cs, NotificationHandlerIntegrationTests.cs
- **Commit:** a18e655

## Architecture Impact

### Effect Pattern Established

ReconnectionEffect demonstrates the effect pattern for this codebase:
- Subscribe to store state observable
- React to state transitions (not just current state)
- Trigger side effects (async operations, service calls)
- No return value; pure side effect

This pattern will be used for other cross-cutting concerns.

### Data Flow

```
SignalR → ConnectionEventDispatcher → ConnectionStore → ReconnectionEffect
                                                              ↓
                                          SessionService.StartSessionAsync
                                          StreamResumeService.TryResumeStreamAsync
```

## Files Changed

| File | Change |
|------|--------|
| `WebChat.Client/State/Hub/ReconnectionEffect.cs` | Created - effect that handles reconnection |
| `WebChat.Client/Services/Streaming/StreamResumeService.cs` | Updated - dispatch actions instead of direct mutations |
| `WebChat.Client/Components/Chat/ChatContainer.razor` | Simplified - removed reconnection handling |
| `WebChat.Client/Program.cs` | Updated - register and activate ReconnectionEffect |
| `Tests/Unit/WebChat.Client/State/ReconnectionEffectTests.cs` | Created - 5 unit tests |
| `Tests/Integration/WebChat/Client/StreamResumeServiceIntegrationTests.cs` | Updated - constructor changes |
| `Tests/Integration/WebChat/Client/NotificationHandlerIntegrationTests.cs` | Updated - constructor changes |

## Next Phase Readiness

Phase 4 (SignalR Integration) is now complete:
- Plan 01: HubEventDispatcher bridges SignalR notifications to store actions
- Plan 02: ConnectionEventDispatcher bridges HubConnection events to ConnectionStore
- Plan 03: ReconnectionEffect handles stream resumption through stores

Ready for Phase 5 (Component Architecture) which will:
- Migrate remaining ChatStateManager usage to stores
- Refactor components to subscribe directly to stores
- Eliminate prop drilling through store-based data flow

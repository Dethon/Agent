# WebChat Stack Refactoring

## What This Is

A maintainability refactor of the WebChat stack (Blazor client and SignalR server) establishing unidirectional data flow with Flux-inspired state management. The refactor achieved code that's easier to follow, extend, and maintains Clean Architecture boundaries.

## Core Value

State flows in one direction — down from stores, up via events — making it obvious where state lives and how changes propagate.

## Current State (v1.0 Shipped)

**Shipped:** 2026-01-20

The WebChat stack now has:
- 5 independent state stores (Topics, Messages, Streaming, Connection, Approval) with 34 action handlers
- Store<T> infrastructure with BehaviorSubject for reactive subscriptions
- HubEventDispatcher bridging SignalR events to typed store actions
- 50ms throttled rendering via RenderCoordinator preventing UI freezes
- ChatContainer reduced from 305 to 28 lines with effect-based coordination
- INotifier properly layered in Infrastructure (adapter pattern)
- ChatStateManager and StreamingCoordinator deleted (replaced by stores)

**Stats:**
- 4,324 lines C#/Razor in WebChat.Client
- 7 phases, 24 plans executed
- Net -172 LOC (cleaner code)

## Requirements

### Validated

**v1.0 Refactoring Goals:**
- ✓ Establish unidirectional data flow in WebChat.Client — v1.0
- ✓ Refactor StreamingCoordinator to single-responsibility components — v1.0 (deleted, replaced by stores)
- ✓ Consolidate scattered state into centralized stores — v1.0 (5 stores)
- ✓ Move INotifier implementation from Agent to Infrastructure — v1.0 (adapter pattern)
- ✓ Ensure all WebChat code respects Domain → Infrastructure → Agent layering — v1.0

**Preserved Functionality:**
- ✓ Real-time message streaming via SignalR — existing, preserved
- ✓ Topic-based conversations with persistence — existing, preserved
- ✓ Stream resumption after disconnection — existing, enhanced with ReconnectionEffect
- ✓ Multi-agent selection — existing, preserved
- ✓ Tool approval flow via WebChat — existing, preserved with ApprovalStore
- ✓ Message history loading — existing, preserved

### Active

(No active requirements — milestone complete)

### Out of Scope

- New WebChat features — this was refactoring, not feature work
- Telegram or CLI interfaces — different codepaths, not affected
- MCP server changes — orthogonal concern
- Performance optimization beyond 50ms throttle — focus was structure

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Custom stores over Fluxor | Avoids 300KB bundle, maintains control over SignalR integration | ✓ Good |
| C# records for state | Built-in immutability, structural equality | ✓ Good |
| BehaviorSubject for Store | Replays current value to late subscribers, composable operators | ✓ Good |
| RegisterHandler on Dispatcher | Components inject IDispatcher (dispatch-only), stores inject Dispatcher for registration | ✓ Good |
| CSS-only visual feedback | Blinking cursor and typing indicator use CSS animations for hardware acceleration | ✓ Good |
| Sample over Throttle | Rx.NET Throttle is debounce; Sample emits at fixed intervals for render ticks | ✓ Good |
| Adapter pattern for INotifier | IHubNotificationSender abstracts hub context, enables INotifier in Infrastructure | ✓ Good |
| Fire-and-forget effects | Effects register sync handlers; async work runs fire-and-forget | ✓ Good |
| Static BufferRebuildUtility | Pure functions extracted for buffer rebuild, no instance needed | ✓ Good |

## Constraints

- **Backwards compatibility**: All existing WebChat functionality continues working ✓
- **No API changes**: SignalR hub method signatures remain stable ✓
- **Incremental**: Changes deployed incrementally via 24 plans across 7 phases ✓

## Tech Debt (Minor)

- Orphaned `IChatNotificationHandler` interface (superseded by HubEventDispatcher, can be deleted)
- 2 flaky tests in StreamResumeServiceTests (pre-existing, not regressions)

---
*Last updated: 2026-01-20 after v1.0 milestone*

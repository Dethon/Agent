# Agent WebChat

## What This Is

A browser-based chat interface for interacting with AI agents. Users pick a username, join shared topics, and chat with agents that know who they're talking to. Built on Blazor WebAssembly with SignalR for real-time communication and Flux-inspired state management.

## Core Value

People can have personalized conversations with agents in shared topics — the agent knows who's talking and responds accordingly.

## Current Milestone: v1.1 Users in Web UI

**Goal:** Add user identity and shared conversations to WebChat.

**Target features:**
- Lightweight user identity (username + avatar stored locally, no authentication)
- User attribution on messages (see who sent each message)
- Shared topics (all users see all topics, real-time message sync)
- Agent personalization (username injected into prompts)

## Requirements

### Validated

**v1.0 Refactoring (Shipped 2026-01-20):**
- ✓ Unidirectional data flow in WebChat.Client — v1.0
- ✓ 5 centralized state stores (Topics, Messages, Streaming, Connection, Approval) — v1.0
- ✓ 50ms throttled rendering via RenderCoordinator — v1.0
- ✓ HubEventDispatcher bridging SignalR to typed store actions — v1.0
- ✓ Clean Architecture boundaries (INotifier in Infrastructure) — v1.0

**Preserved Functionality:**
- ✓ Real-time message streaming via SignalR
- ✓ Topic-based conversations with persistence
- ✓ Stream resumption after disconnection
- ✓ Multi-agent selection
- ✓ Tool approval flow
- ✓ Message history loading

### Active

- [ ] User can set username (stored locally, persists across sessions)
- [ ] User can select avatar from hardcoded options
- [ ] Messages display sender's username and avatar
- [ ] Topics are shared across all users
- [ ] Messages broadcast to all users in a topic in real-time
- [ ] Agent prompts include username for personalization

### Out of Scope

- Authentication/passwords — lightweight identity only, no auth system
- User accounts on server — usernames stored client-side only
- Private topics — all topics visible to all users
- Direct messaging between users — agent-mediated conversations only
- Telegram or CLI user features — WebChat only for this milestone

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
*Last updated: 2026-01-21 after v1.1 milestone started*
